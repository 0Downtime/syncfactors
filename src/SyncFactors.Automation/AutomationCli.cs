using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging.Abstractions;
using SyncFactors.Contracts;
using SyncFactors.Domain;
using SyncFactors.Infrastructure;

namespace SyncFactors.Automation;

public static class AutomationCli
{
    public static async Task<int> RunAsync(string[] args, TextWriter output, CancellationToken cancellationToken)
    {
        try
        {
            var options = AutomationOptions.Parse(args);
            var scenarios = await ScenarioLoader.LoadAsync(options.ScenarioPatterns, options.Tags, cancellationToken);
            if (scenarios.Count == 0)
            {
                throw new InvalidOperationException("No automation scenarios matched the requested path/tag filters.");
            }

            foreach (var scenario in scenarios)
            {
                AutomationRiskPolicy.EnsureAllowed(scenario, options);
            }

            await using var runner = new AutomationRunner(options, output);
            await AutomationConsole.WriteInfoAsync(output, $"Loaded {scenarios.Count} scenario(s).");
            var report = await runner.RunAsync(scenarios, cancellationToken);
            await AutomationConsole.WriteInfoAsync(output, $"Writing reports to {options.ReportPath} and {Path.ChangeExtension(options.ReportPath, ".json")}.");
            await AutomationReportWriter.WriteAsync(report, options.ReportPath, cancellationToken);
            AutomationReportWriter.WriteSummary(output, report, options.ReportPath);
            return report.Passed ? 0 : 1;
        }
        catch (Exception ex)
        {
            await AutomationConsole.WriteFailureAsync(output, $"Automation failed: {ex.Message}");
            foreach (var diagnosis in AutomationFailureAnalyzer.Analyze(ex.Message, null))
            {
                await AutomationConsole.WriteActionAsync(output, $"Likely failed: {diagnosis.LikelyFailure}");
                await AutomationConsole.WriteActionAsync(output, $"Check: {diagnosis.Check}");
            }
            return 1;
        }
    }
}

public sealed record AutomationOptions(
    IReadOnlyList<string> ScenarioPatterns,
    string ReportPath,
    Uri ApiUrl,
    Uri MockUrl,
    string Username,
    string Password,
    bool AllowAdReset,
    IReadOnlySet<string> Tags,
    string? ConfigPath,
    string? MappingConfigPath,
    TimeSpan Timeout,
    bool IncludeDestructive,
    bool IncludeScale,
    bool IncludeRecovery,
    bool Idempotency)
{
    public static AutomationOptions Parse(string[] args)
    {
        var values = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        var flags = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        for (var index = 0; index < args.Length; index++)
        {
            var key = args[index];
            if (!key.StartsWith("--", StringComparison.Ordinal))
            {
                throw new InvalidOperationException($"Unsupported argument '{key}'.");
            }

            key = key[2..];
            if (IsFlag(key))
            {
                flags.Add(key);
                continue;
            }

            if (index + 1 >= args.Length)
            {
                throw new InvalidOperationException($"Expected value after '--{key}'.");
            }

            if (!values.TryGetValue(key, out var list))
            {
                list = [];
                values[key] = list;
            }

            list.Add(args[++index]);
        }

        var scenarioPatterns = ReadMany(values, "scenario");
        if (scenarioPatterns.Count == 0)
        {
            scenarioPatterns = ["config/automation/*.json"];
        }

        var reportPath = ReadOne(values, "report")
            ?? Path.Combine("state", "runtime", "automation-reports", $"e2e-{DateTimeOffset.UtcNow:yyyyMMddHHmmss}.md");
        var apiUrl = new Uri(ReadOne(values, "api-url") ?? Environment.GetEnvironmentVariable("SYNCFACTORS_AUTOMATION_API_URL") ?? "https://127.0.0.1:5087");
        var mockUrl = new Uri(ReadOne(values, "mock-url") ?? Environment.GetEnvironmentVariable("SYNCFACTORS_AUTOMATION_MOCK_URL") ?? "http://127.0.0.1:18080");
        var username = ReadOne(values, "username") ?? Environment.GetEnvironmentVariable("SYNCFACTORS_AUTOMATION_USERNAME") ?? string.Empty;
        var password = ReadOne(values, "password") ?? Environment.GetEnvironmentVariable("SYNCFACTORS_AUTOMATION_PASSWORD") ?? string.Empty;
        var timeoutMinutesValue = ReadOne(values, "timeout-minutes") ?? "10";
        if (!double.TryParse(timeoutMinutesValue, out var timeoutMinutes) || timeoutMinutes <= 0)
        {
            throw new InvalidOperationException("--timeout-minutes must be a positive number.");
        }

        return new AutomationOptions(
            ScenarioPatterns: scenarioPatterns,
            ReportPath: Path.GetFullPath(reportPath),
            ApiUrl: apiUrl,
            MockUrl: mockUrl,
            Username: username,
            Password: password,
            AllowAdReset: flags.Contains("allow-ad-reset"),
            Tags: ParseTags(ReadMany(values, "tags")),
            ConfigPath: ReadOne(values, "config"),
            MappingConfigPath: ReadOne(values, "mapping"),
            Timeout: TimeSpan.FromMinutes(timeoutMinutes),
            IncludeDestructive: flags.Contains("include-destructive"),
            IncludeScale: flags.Contains("include-scale"),
            IncludeRecovery: flags.Contains("include-recovery"),
            Idempotency: flags.Contains("idempotency"));
    }

    private static bool IsFlag(string key) =>
        string.Equals(key, "allow-ad-reset", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(key, "include-destructive", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(key, "include-scale", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(key, "include-recovery", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(key, "idempotency", StringComparison.OrdinalIgnoreCase);

    private static string? ReadOne(Dictionary<string, List<string>> values, string key) =>
        values.TryGetValue(key, out var list) && list.Count > 0 ? list[^1] : null;

    private static IReadOnlyList<string> ReadMany(Dictionary<string, List<string>> values, string key) =>
        values.TryGetValue(key, out var list)
            ? list.SelectMany(value => value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)).ToArray()
            : [];

    private static IReadOnlySet<string> ParseTags(IEnumerable<string> tags) =>
        tags.Where(tag => !string.IsNullOrWhiteSpace(tag)).ToHashSet(StringComparer.OrdinalIgnoreCase);
}

public static class ScenarioLoader
{
    public static async Task<IReadOnlyList<AutomationScenario>> LoadAsync(
        IReadOnlyList<string> patterns,
        IReadOnlySet<string> tags,
        CancellationToken cancellationToken)
    {
        var paths = ExpandPatterns(patterns);
        var scenarios = new List<AutomationScenario>();
        foreach (var path in paths)
        {
            var json = await File.ReadAllTextAsync(path, cancellationToken);
            var loaded = JsonSerializer.Deserialize<AutomationScenario>(json, AutomationCliJson.Options)
                ?? throw new InvalidOperationException($"Scenario '{path}' did not deserialize.");
            var scenario = loaded with
            {
                SourcePath = path,
                Tags = loaded.Tags ?? [],
                Iterations = loaded.Iterations ?? [],
                ExpectedAuditEvents = loaded.ExpectedAuditEvents ?? [],
                RiskLevel = string.IsNullOrWhiteSpace(loaded.RiskLevel) ? AutomationRiskLevels.Safe : loaded.RiskLevel
            };
            Validate(scenario);
            if (tags.Count == 0 || scenario.Tags.Any(tags.Contains))
            {
                scenarios.Add(scenario);
            }
        }

        return scenarios;
    }

    private static IReadOnlyList<string> ExpandPatterns(IReadOnlyList<string> patterns)
    {
        var paths = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var pattern in patterns)
        {
            var fullPattern = Path.GetFullPath(pattern);
            if (!pattern.Contains('*', StringComparison.Ordinal) && !pattern.Contains('?', StringComparison.Ordinal))
            {
                if (!File.Exists(fullPattern))
                {
                    throw new FileNotFoundException($"Scenario file '{fullPattern}' was not found.", fullPattern);
                }

                paths.Add(fullPattern);
                continue;
            }

            var directory = Path.GetDirectoryName(fullPattern);
            var search = Path.GetFileName(fullPattern);
            if (string.IsNullOrWhiteSpace(directory) || string.IsNullOrWhiteSpace(search) || !Directory.Exists(directory))
            {
                continue;
            }

            foreach (var match in Directory.GetFiles(directory, search))
            {
                paths.Add(Path.GetFullPath(match));
            }
        }

        return paths.ToArray();
    }

    private static void Validate(AutomationScenario scenario)
    {
        if (string.IsNullOrWhiteSpace(scenario.Name))
        {
            throw new InvalidOperationException($"Scenario '{scenario.SourcePath}' must define name.");
        }

        if (scenario.Iterations.Count == 0)
        {
            throw new InvalidOperationException($"Scenario '{scenario.Name}' must contain at least one iteration.");
        }

        if (!AutomationRiskLevels.All.Contains(scenario.RiskLevel))
        {
            throw new InvalidOperationException($"Scenario '{scenario.Name}' has unsupported riskLevel '{scenario.RiskLevel}'. Supported values: {string.Join(", ", AutomationRiskLevels.All)}.");
        }

        var expectedOrder = 1;
        foreach (var iteration in scenario.Iterations.OrderBy(iteration => iteration.Order))
        {
            if (iteration.Order != expectedOrder++)
            {
                throw new InvalidOperationException($"Scenario '{scenario.Name}' has non-contiguous iteration order.");
            }

            if (iteration.Expectation is null)
            {
                throw new InvalidOperationException($"Scenario '{scenario.Name}' iteration {iteration.Order} is missing expectation.");
            }
        }
    }
}

public static class AutomationRiskLevels
{
    public const string Safe = "safe";
    public const string Destructive = "destructive";
    public const string Scale = "scale";
    public const string Recovery = "recovery";

    public static IReadOnlySet<string> All { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        Safe,
        Destructive,
        Scale,
        Recovery
    };
}

public static class AutomationRiskPolicy
{
    public static void EnsureAllowed(AutomationScenario scenario, AutomationOptions options)
    {
        if (string.Equals(scenario.RiskLevel, AutomationRiskLevels.Safe, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        if (string.Equals(scenario.RiskLevel, AutomationRiskLevels.Destructive, StringComparison.OrdinalIgnoreCase) &&
            options.IncludeDestructive)
        {
            return;
        }

        if (string.Equals(scenario.RiskLevel, AutomationRiskLevels.Scale, StringComparison.OrdinalIgnoreCase) &&
            options.IncludeScale)
        {
            return;
        }

        if (string.Equals(scenario.RiskLevel, AutomationRiskLevels.Recovery, StringComparison.OrdinalIgnoreCase) &&
            options.IncludeRecovery)
        {
            return;
        }

        throw new InvalidOperationException(
            $"Scenario '{scenario.Name}' has riskLevel '{scenario.RiskLevel}'. Pass --include-{scenario.RiskLevel} to run it.");
    }
}

public sealed class AutomationRunner(AutomationOptions options, TextWriter output) : IAsyncDisposable
{
    private readonly HttpClient _apiClient = new(new HttpClientHandler
    {
        ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator
    });
    private readonly HttpClient _mockClient = new();

    public async Task<AutomationRunReport> RunAsync(IReadOnlyList<AutomationScenario> scenarios, CancellationToken cancellationToken)
    {
        _apiClient.BaseAddress = options.ApiUrl;
        _mockClient.BaseAddress = options.MockUrl;
        await AutomationConsole.WriteStageAsync(output, $"API URL: {options.ApiUrl}");
        await AutomationConsole.WriteStageAsync(output, $"Mock URL: {options.MockUrl}");
        await AutomationConsole.WriteStageAsync(output, "Validating local automation safety settings.");
        ValidateLocalConfigurationSafety();
        await AutomationConsole.WriteStageAsync(output, "Authenticating to SyncFactors API.");
        await AuthenticateAsync(cancellationToken);
        await AutomationConsole.WritePassAsync(output, "Authentication completed.");

        var scenarioReports = new List<AutomationScenarioReport>();
        foreach (var scenario in scenarios)
        {
            await AutomationConsole.WriteStageAsync(output, $"Running scenario: {scenario.Name} risk={scenario.RiskLevel}");
            scenarioReports.Add(await RunScenarioAsync(scenario, cancellationToken));
        }

        return new AutomationRunReport(
            StartedAtUtc: scenarioReports.Min(report => report.StartedAtUtc),
            CompletedAtUtc: DateTimeOffset.UtcNow,
            Passed: scenarioReports.All(report => report.Passed),
            Scenarios: scenarioReports);
    }

    private async Task AuthenticateAsync(CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(options.Username) || string.IsNullOrWhiteSpace(options.Password))
        {
            throw new InvalidOperationException("Automation username/password are required. Set SYNCFACTORS_AUTOMATION_USERNAME and SYNCFACTORS_AUTOMATION_PASSWORD or pass --username/--password.");
        }

        var deadline = DateTimeOffset.UtcNow.Add(options.Timeout);
        Exception? lastFailure = null;
        while (DateTimeOffset.UtcNow < deadline)
        {
            try
            {
                var response = await _apiClient.PostAsJsonAsync(
                    "/api/session/login",
                    new { username = options.Username, password = options.Password, rememberMe = false, returnUrl = (string?)null },
                    cancellationToken);
                await EnsureSuccessAsync(response, "API login", cancellationToken);
                return;
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or InvalidOperationException)
            {
                lastFailure = ex;
                await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken);
            }
        }

        throw new TimeoutException($"Timed out waiting for SyncFactors API login at {options.ApiUrl}. Last failure: {lastFailure?.Message}");
    }

    private void ValidateLocalConfigurationSafety()
    {
        var loader = new SyncFactorsConfigurationLoader(new SyncFactorsConfigPathResolver(options.ConfigPath, options.MappingConfigPath));
        var config = loader.GetSyncConfig();
        if (!config.Sync.RealSyncEnabled)
        {
            throw new InvalidOperationException("Real AD E2E automation requires sync.realSyncEnabled=true in the selected mock SuccessFactors + real AD config.");
        }

        var configuredSuccessFactorsUrl = new Uri(config.SuccessFactors.BaseUrl);
        if (!string.Equals(configuredSuccessFactorsUrl.Host, options.MockUrl.Host, StringComparison.OrdinalIgnoreCase) ||
            configuredSuccessFactorsUrl.Port != options.MockUrl.Port)
        {
            throw new InvalidOperationException(
                $"Real AD E2E automation only supports the mock SuccessFactors profile. Configured SuccessFactors baseUrl is '{config.SuccessFactors.BaseUrl}', but the runner mock URL is '{options.MockUrl}'.");
        }

        if (string.IsNullOrWhiteSpace(config.Ad.DefaultActiveOu) ||
            string.IsNullOrWhiteSpace(config.Ad.PrehireOu) ||
            string.IsNullOrWhiteSpace(config.Ad.GraveyardOu))
        {
            throw new InvalidOperationException("Real AD E2E automation requires configured active, prehire, and graveyard AD test OUs.");
        }
    }

    private async Task<AutomationScenarioReport> RunScenarioAsync(AutomationScenario scenario, CancellationToken cancellationToken)
    {
        var startedAt = DateTimeOffset.UtcNow;
        var failures = new List<string>();
        var iterationReports = new List<AutomationIterationReport>();
        var preflightResults = await RunPreflightAsync(scenario, cancellationToken);
        failures.AddRange(preflightResults.Where(result => !result.Passed).Select(result => $"Preflight {result.Name} failed: {result.Message}"));
        if (failures.Count > 0)
        {
            await WriteFailureDiagnosticsAsync(scenario, failures, null, null);
            return new AutomationScenarioReport(
                Name: scenario.Name,
                SourcePath: scenario.SourcePath,
                RiskLevel: scenario.RiskLevel,
                StartedAtUtc: startedAt,
                CompletedAtUtc: DateTimeOffset.UtcNow,
                Passed: false,
                Failures: failures,
                Preflight: preflightResults,
                BaselineAdSnapshot: null,
                FinalAdSnapshot: null,
                Iterations: iterationReports,
                Diagnostics: new AutomationScenarioDiagnostics(LastQueueStatus: null, LastRunId: null, LastWorkerHeartbeat: null, AdDiff: []));
        }

        var baselineSnapshot = await CaptureManagedOuSnapshotAsync(scenario, "baseline", cancellationToken);

        if (scenario.ResetMockBeforeScenario)
        {
            await AutomationConsole.WriteStageAsync(output, $"[{scenario.Name}] Resetting mock SuccessFactors runtime workers.");
            await EnsureSuccessAsync(await _mockClient.PostAsync("/api/admin/reset", null, cancellationToken), "mock reset", cancellationToken);
            await AutomationConsole.WritePassAsync(output, $"[{scenario.Name}] Mock reset completed.");
            await IsolateMockWorkersAsync(scenario, cancellationToken);
        }

        if (scenario.ResetAdBeforeScenario)
        {
            if (!options.AllowAdReset)
            {
                throw new InvalidOperationException($"Scenario '{scenario.Name}' requires AD reset. Re-run with --allow-ad-reset after confirming the configured AD OUs are test-only.");
            }

            await AutomationConsole.WriteWarningAsync(output, $"[{scenario.Name}] Queueing destructive AD test OU reset.");
            var reset = await QueueAndWaitAsync("/api/runs/delete-all", new { confirmationText = "DELETE ALL USERS" }, cancellationToken);
            if (!string.Equals(reset.Status, "Completed", StringComparison.OrdinalIgnoreCase))
            {
                failures.Add($"AD reset failed: {reset.ErrorMessage ?? reset.Status}");
            }
            else
            {
                await AutomationConsole.WritePassAsync(output, $"[{scenario.Name}] AD reset completed. run={reset.RunId}");
            }
        }

        foreach (var iteration in scenario.Iterations.OrderBy(iteration => iteration.Order))
        {
            await AutomationConsole.WriteStageAsync(output, $"[{scenario.Name}] Iteration {iteration.Order}: {iteration.Name ?? $"Iteration {iteration.Order}"}");
            await AutomationConsole.WriteInfoAsync(output, $"[{scenario.Name}] Applying {iteration.Mutations?.Count ?? 0} mock worker mutation(s).");
            await ApplyMutationsAsync(iteration.Mutations ?? [], cancellationToken);
            await AutomationConsole.WriteStageAsync(output, $"[{scenario.Name}] Queueing live sync run.");
            var queued = await QueueAndWaitAsync(
                "/api/runs",
                new { dryRun = false, mode = scenario.SyncMode, runTrigger = "Automation", requestedBy = "Automation" },
                cancellationToken);
            var iterationFailures = new List<string>();
            if (!string.Equals(queued.Status, "Completed", StringComparison.OrdinalIgnoreCase) || string.IsNullOrWhiteSpace(queued.RunId))
            {
                iterationFailures.Add($"Run queue request {queued.RequestId} ended with status {queued.Status}: {queued.ErrorMessage}");
            }

            if (!string.IsNullOrWhiteSpace(scenario.ExpectedQueueStatus) &&
                !string.Equals(queued.Status, scenario.ExpectedQueueStatus, StringComparison.OrdinalIgnoreCase))
            {
                iterationFailures.Add($"expected queue status {scenario.ExpectedQueueStatus} but found {queued.Status}.");
            }

            JsonObject? runDetail = null;
            IReadOnlyList<JsonObject> entries = [];
            if (!string.IsNullOrWhiteSpace(queued.RunId))
            {
                await AutomationConsole.WriteInfoAsync(output, $"[{scenario.Name}] Loading run detail and entries for {queued.RunId}.");
                runDetail = await GetJsonObjectAsync($"/api/runs/{queued.RunId}", cancellationToken);
                entries = await GetRunEntriesAsync(queued.RunId, cancellationToken);
                iterationFailures.AddRange(ValidateIteration(iteration, runDetail, entries));
                if (!string.IsNullOrWhiteSpace(scenario.ExpectedRunStatus))
                {
                    var runStatus = ReadString(runDetail["run"]?.AsObject(), "status");
                    if (!string.Equals(runStatus, scenario.ExpectedRunStatus, StringComparison.OrdinalIgnoreCase))
                    {
                        iterationFailures.Add($"expected scenario run status {scenario.ExpectedRunStatus} but found {runStatus ?? "(missing)"}.");
                    }
                }
            }

            var iterationSummary = $"[{scenario.Name}] Iteration {iteration.Order} completed. queue={queued.Status} run={queued.RunId ?? "(none)"} failures={iterationFailures.Count} buckets={FormatCounts(ExtractBucketCounts(runDetail, entries))}";
            if (iterationFailures.Count == 0)
            {
                await AutomationConsole.WritePassAsync(output, iterationSummary);
            }
            else
            {
                await AutomationConsole.WriteFailureAsync(output, iterationSummary);
                await WriteFailureDiagnosticsAsync(scenario, iterationFailures, iteration, queued.RunId);
            }
            failures.AddRange(iterationFailures.Select(failure => $"Iteration {iteration.Order}: {failure}"));
            iterationReports.Add(new AutomationIterationReport(
                Order: iteration.Order,
                Name: iteration.Name ?? $"Iteration {iteration.Order}",
                RequestId: queued.RequestId,
                RunId: queued.RunId,
                QueueStatus: queued.Status,
                BucketCounts: ExtractBucketCounts(runDetail, entries),
                Failures: iterationFailures));
        }

        await AutomationConsole.WriteStageAsync(output, $"[{scenario.Name}] Verifying final AD expectations.");
        failures.AddRange(await VerifyAdAsync(scenario, cancellationToken));
        var finalSnapshot = await CaptureManagedOuSnapshotAsync(scenario, "final", cancellationToken);
        var config = new SyncFactorsConfigurationLoader(new SyncFactorsConfigPathResolver(options.ConfigPath, options.MappingConfigPath)).GetSyncConfig();
        var adDiff = AutomationAdDiff.Build(ResolveExpectedUsers(scenario.FinalExpectation?.ExpectedAdUsers ?? scenario.FinalExpectation?.DirectoryUsers ?? [], config), finalSnapshot);
        failures.AddRange(adDiff.Where(diff => diff.Severity.Equals("Failure", StringComparison.OrdinalIgnoreCase)).Select(diff => diff.Message));
        if (options.Idempotency)
        {
            failures.AddRange(await RunIdempotencyCheckAsync(scenario, cancellationToken));
        }

        if (scenario.ExpectedDurationSeconds is { } expectedDurationSeconds &&
            DateTimeOffset.UtcNow - startedAt > TimeSpan.FromSeconds(expectedDurationSeconds))
        {
            failures.Add($"Scenario exceeded expectedDurationSeconds={expectedDurationSeconds}.");
        }

        var lastIteration = iterationReports.LastOrDefault();
        if (failures.Count == 0)
        {
            await AutomationConsole.WritePassAsync(output, $"[{scenario.Name}] Scenario completed. result=PASSED failures=0");
        }
        else
        {
            await AutomationConsole.WriteFailureAsync(output, $"[{scenario.Name}] Scenario completed. result=FAILED failures={failures.Count}");
            await WriteFailureDiagnosticsAsync(scenario, failures, null, lastIteration?.RunId);
        }
        return new AutomationScenarioReport(
            Name: scenario.Name,
            SourcePath: scenario.SourcePath,
            RiskLevel: scenario.RiskLevel,
            StartedAtUtc: startedAt,
            CompletedAtUtc: DateTimeOffset.UtcNow,
            Passed: failures.Count == 0,
            Failures: failures,
            Preflight: preflightResults,
            BaselineAdSnapshot: baselineSnapshot,
            FinalAdSnapshot: finalSnapshot,
            Iterations: iterationReports,
            Diagnostics: new AutomationScenarioDiagnostics(
                LastQueueStatus: lastIteration?.QueueStatus,
                LastRunId: lastIteration?.RunId,
                LastWorkerHeartbeat: preflightResults.FirstOrDefault(result => result.Name.Equals("worker-heartbeat", StringComparison.OrdinalIgnoreCase))?.Message,
                AdDiff: adDiff));
    }

    private async Task<IReadOnlyList<AutomationPreflightResult>> RunPreflightAsync(AutomationScenario scenario, CancellationToken cancellationToken)
    {
        var preflight = scenario.Preflight ?? new AutomationPreflightOptions();
        var results = new List<AutomationPreflightResult>();
        await output.WriteLineAsync($"[{scenario.Name}] Running production preflight checks.");

        if (preflight.ApiHealth)
        {
            results.Add(await ProbeJsonAsync(_apiClient, "/healthz", "api-healthz", "API health endpoint", cancellationToken));
            results.Add(await ProbeJsonAsync(_apiClient, "/api/health", "api-dependency-health", "API dependency health", cancellationToken, expectedStatus: scenario.ExpectedHealth));
        }

        if (preflight.MockHealth)
        {
            results.Add(await ProbeJsonAsync(_mockClient, "/healthz", "mock-healthz", "mock SuccessFactors health endpoint", cancellationToken));
        }

        if (preflight.WorkerHeartbeat)
        {
            results.Add(await ProbeWorkerHeartbeatAsync(scenario.ExpectedHealth, cancellationToken));
        }

        if (preflight.ActiveDirectory)
        {
            try
            {
                var snapshot = await CaptureManagedOuSnapshotAsync(scenario, "preflight", cancellationToken);
                results.Add(new AutomationPreflightResult("active-directory", true, $"listed {snapshot.TotalUsers} managed OU user(s)", DateTimeOffset.UtcNow));
            }
            catch (Exception ex)
            {
                results.Add(new AutomationPreflightResult("active-directory", false, ex.Message, DateTimeOffset.UtcNow));
            }
        }

        foreach (var result in results)
        {
            var message = $"[{scenario.Name}] Preflight {result.Name}: {(result.Passed ? "PASS" : "FAIL")} - {result.Message}";
            if (result.Passed)
            {
                await AutomationConsole.WritePassAsync(output, message);
            }
            else
            {
                await AutomationConsole.WriteFailureAsync(output, message);
            }
        }

        return results;
    }

    private static async Task<AutomationPreflightResult> ProbeJsonAsync(
        HttpClient client,
        string path,
        string name,
        string label,
        CancellationToken cancellationToken,
        string? expectedStatus = null)
    {
        try
        {
            var payload = await GetJsonObjectAsync(client, path, cancellationToken);
            var status = ReadString(payload, "status");
            var passed = string.IsNullOrWhiteSpace(expectedStatus) ||
                string.Equals(status, expectedStatus, StringComparison.OrdinalIgnoreCase);
            return new AutomationPreflightResult(name, passed, $"{label} returned status={status ?? "ok"}", DateTimeOffset.UtcNow);
        }
        catch (Exception ex)
        {
            return new AutomationPreflightResult(name, false, ex.Message, DateTimeOffset.UtcNow);
        }
    }

    private async Task<AutomationPreflightResult> ProbeWorkerHeartbeatAsync(string? expectedStatus, CancellationToken cancellationToken)
    {
        try
        {
            var payload = await GetJsonObjectAsync("/api/dashboard/health", cancellationToken);
            var status = ReadString(payload, "status");
            var probes = payload["probes"]?.AsArray().OfType<JsonObject>() ?? [];
            var workerProbe = probes.FirstOrDefault(probe =>
                (ReadString(probe, "dependency") ?? string.Empty).Contains("worker", StringComparison.OrdinalIgnoreCase));
            var message = workerProbe is null
                ? $"dashboard health status={status ?? "(missing)"}, no explicit worker probe found"
                : $"dashboard health status={status ?? "(missing)"}, worker={ReadString(workerProbe, "status") ?? "(missing)"} observedAt={ReadString(workerProbe, "observedAt") ?? "(missing)"} stale={workerProbe["isStale"]?.GetValue<bool>()}";
            var passed = !string.Equals(status, DependencyHealthStates.Unhealthy, StringComparison.OrdinalIgnoreCase) &&
                (string.IsNullOrWhiteSpace(expectedStatus) || string.Equals(status, expectedStatus, StringComparison.OrdinalIgnoreCase));
            return new AutomationPreflightResult("worker-heartbeat", passed, message, DateTimeOffset.UtcNow);
        }
        catch (Exception ex)
        {
            return new AutomationPreflightResult("worker-heartbeat", false, ex.Message, DateTimeOffset.UtcNow);
        }
    }

    private async Task<AutomationAdSnapshot> CaptureManagedOuSnapshotAsync(AutomationScenario scenario, string phase, CancellationToken cancellationToken)
    {
        var loader = new SyncFactorsConfigurationLoader(new SyncFactorsConfigPathResolver(options.ConfigPath, options.MappingConfigPath));
        var config = loader.GetSyncConfig();
        var mappingProvider = new AttributeMappingProvider(loader, NullLogger<AttributeMappingProvider>.Instance);
        using var pool = new ActiveDirectoryConnectionPool();
        var gateway = new ActiveDirectoryGateway(loader, mappingProvider, pool, NullLogger<ActiveDirectoryGateway>.Instance);
        var users = new List<AutomationAdSnapshotUser>();
        foreach (var ou in GetManagedOus(config))
        {
            await AutomationConsole.WriteInfoAsync(output, $"[{scenario.Name}] Capturing {phase} AD snapshot for {ou.Name}: {ou.DistinguishedName}");
            var ouUsers = await gateway.ListUsersInOuAsync(ou.DistinguishedName, cancellationToken);
            users.AddRange(ouUsers.Select(user => AutomationAdSnapshotUser.FromDirectoryUser(ou.Name, ou.DistinguishedName, user, config.Ad.IdentityAttribute)));
        }

        var duplicates = AutomationAdDiff.FindDuplicateDirectoryValues(users);
        foreach (var duplicate in duplicates)
        {
            await AutomationConsole.WriteWarningAsync(output, $"[{scenario.Name}] AD snapshot warning: {duplicate.Message}");
        }

        return new AutomationAdSnapshot(phase, DateTimeOffset.UtcNow, users.Count, users, duplicates);
    }

    private static IReadOnlyList<(string Name, string DistinguishedName)> GetManagedOus(SyncFactorsConfigDocument config)
    {
        return new[]
        {
            ("active", config.Ad.DefaultActiveOu),
            ("prehire", config.Ad.PrehireOu),
            ("graveyard", config.Ad.GraveyardOu),
            ("leave", config.Ad.LeaveOu)
        }
            .Where(item => !string.IsNullOrWhiteSpace(item.Item2))
            .Select(item => (item.Item1, item.Item2!))
            .DistinctBy(item => item.Item2, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private async Task<IReadOnlyList<string>> RunIdempotencyCheckAsync(AutomationScenario scenario, CancellationToken cancellationToken)
    {
        await AutomationConsole.WriteStageAsync(output, $"[{scenario.Name}] Running idempotency check with one extra live sync.");
        var failures = new List<string>();
        var queued = await QueueAndWaitAsync(
            "/api/runs",
            new { dryRun = false, mode = scenario.SyncMode, runTrigger = "AutomationIdempotency", requestedBy = "Automation" },
            cancellationToken);
        if (!string.Equals(queued.Status, "Completed", StringComparison.OrdinalIgnoreCase) || string.IsNullOrWhiteSpace(queued.RunId))
        {
            failures.Add($"Idempotency queue request {queued.RequestId} ended with status {queued.Status}: {queued.ErrorMessage}");
            return failures;
        }

        var runDetail = await GetJsonObjectAsync($"/api/runs/{queued.RunId}", cancellationToken);
        var entries = await GetRunEntriesAsync(queued.RunId, cancellationToken);
        var counts = ExtractBucketCounts(runDetail, entries);
        foreach (var bucket in new[] { "creates", "updates", "enables", "disables", "graveyardMoves", "deletions" })
        {
            counts.TryGetValue(bucket, out var actual);
            if (actual > 0)
            {
                failures.Add($"Idempotency run expected no {bucket} but found {actual}.");
            }
        }

        return failures;
    }

    private async Task ApplyMutationsAsync(IReadOnlyList<AutomationWorkerMutation> mutations, CancellationToken cancellationToken)
    {
        foreach (var mutation in mutations)
        {
            if (mutation.RemoveFromSource)
            {
                await AutomationConsole.WriteWarningAsync(output, $"  - deleting mock worker {mutation.WorkerId}");
                await EnsureSuccessAsync(await _mockClient.DeleteAsync($"/api/admin/workers/{Uri.EscapeDataString(mutation.WorkerId)}", cancellationToken), $"delete mock worker {mutation.WorkerId}", cancellationToken);
                continue;
            }

            JsonObject worker;
            var exists = true;
            if (mutation.Worker is not null)
            {
                worker = CloneObject(mutation.Worker);
                exists = await MockWorkerExistsAsync(mutation.WorkerId, cancellationToken);
            }
            else
            {
                worker = await GetMockWorkerAsync(mutation.WorkerId, cancellationToken);
            }

            foreach (var pair in mutation.Set ?? new Dictionary<string, string?>())
            {
                SetJsonPath(worker, pair.Key, pair.Value);
            }

            await AutomationConsole.WriteInfoAsync(output, $"  - {(exists ? "updating" : "creating")} mock worker {mutation.WorkerId}");
            var response = exists
                ? await _mockClient.PutAsJsonAsync($"/api/admin/workers/{Uri.EscapeDataString(mutation.WorkerId)}", worker, cancellationToken)
                : await _mockClient.PostAsJsonAsync("/api/admin/workers", worker, cancellationToken);
            await EnsureSuccessAsync(response, $"upsert mock worker {mutation.WorkerId}", cancellationToken);
        }
    }

    private async Task IsolateMockWorkersAsync(AutomationScenario scenario, CancellationToken cancellationToken)
    {
        var keep = GetScenarioWorkerIds(scenario);
        if (keep.Count == 0)
        {
            return;
        }

        var payload = await GetJsonObjectAsync(_mockClient, "/api/admin/workers", cancellationToken);
        var workers = payload["workers"]?.AsArray().OfType<JsonObject>() ?? [];
        var deleted = 0;
        foreach (var worker in workers)
        {
            var workerId = ReadString(worker, "personIdExternal");
            if (string.IsNullOrWhiteSpace(workerId) || keep.Contains(workerId))
            {
                continue;
            }

            var response = await _mockClient.DeleteAsync($"/api/admin/workers/{Uri.EscapeDataString(workerId)}", cancellationToken);
            if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
            {
                continue;
            }

            await EnsureSuccessAsync(response, $"delete mock worker {workerId}", cancellationToken);
            deleted++;
        }

        await AutomationConsole.WriteInfoAsync(output, $"[{scenario.Name}] Isolated mock source to {keep.Count} scenario worker(s); deleted {deleted} non-scenario worker(s).");
    }

    private static IReadOnlySet<string> GetScenarioWorkerIds(AutomationScenario scenario)
    {
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var mutation in scenario.Iterations.SelectMany(iteration => iteration.Mutations ?? []))
        {
            if (!string.IsNullOrWhiteSpace(mutation.WorkerId))
            {
                ids.Add(mutation.WorkerId);
            }
        }

        foreach (var expected in scenario.Iterations
            .Select(iteration => iteration.Expectation)
            .Where(expectation => expectation is not null)
            .SelectMany(expectation => expectation!.WorkerOperations))
        {
            if (!string.IsNullOrWhiteSpace(expected.WorkerId))
            {
                ids.Add(expected.WorkerId);
            }
        }

        foreach (var expected in scenario.FinalExpectation?.ExpectedAdUsers ?? scenario.FinalExpectation?.DirectoryUsers ?? [])
        {
            if (!string.IsNullOrWhiteSpace(expected.WorkerId))
            {
                ids.Add(expected.WorkerId);
            }
        }

        return ids;
    }

    private async Task<bool> MockWorkerExistsAsync(string workerId, CancellationToken cancellationToken)
    {
        var response = await _mockClient.GetAsync($"/api/admin/workers/{Uri.EscapeDataString(workerId)}", cancellationToken);
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return false;
        }

        await EnsureSuccessAsync(response, $"load mock worker {workerId}", cancellationToken);
        return true;
    }

    private async Task<JsonObject> GetMockWorkerAsync(string workerId, CancellationToken cancellationToken)
    {
        var payload = await GetJsonObjectAsync(_mockClient, $"/api/admin/workers/{Uri.EscapeDataString(workerId)}", cancellationToken);
        return payload["worker"]?.AsObject() ?? throw new InvalidOperationException($"Mock worker '{workerId}' response did not contain worker.");
    }

    private async Task<RunQueueRequest> QueueAndWaitAsync(string path, object body, CancellationToken cancellationToken)
    {
        var response = await _apiClient.PostAsJsonAsync(path, body, cancellationToken);
        await EnsureSuccessAsync(response, $"queue {path}", cancellationToken);
        var queued = await ReadRunQueueRequestAsync(response, cancellationToken);
        await AutomationConsole.WriteInfoAsync(output, $"Queued {queued.Mode}. request={queued.RequestId} trigger={queued.RunTrigger} dryRun={queued.DryRun}");
        var deadline = DateTimeOffset.UtcNow.Add(options.Timeout);
        var lastStatus = queued.Status;
        while (DateTimeOffset.UtcNow < deadline)
        {
            var latest = await GetQueueRequestAsync(queued.RequestId, cancellationToken);
            if (!string.Equals(latest.Status, lastStatus, StringComparison.OrdinalIgnoreCase))
            {
                await AutomationConsole.WriteInfoAsync(output, $"  request={latest.RequestId} status={latest.Status} run={latest.RunId ?? "(pending)"}");
                lastStatus = latest.Status;
            }

            if (IsTerminal(latest.Status))
            {
                return latest;
            }

            await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken);
        }

        throw new TimeoutException($"Timed out waiting for queue request {queued.RequestId}.");
    }

    private async Task<RunQueueRequest> GetQueueRequestAsync(string requestId, CancellationToken cancellationToken)
    {
        var payload = await GetJsonObjectAsync($"/api/runs/queue/{Uri.EscapeDataString(requestId)}", cancellationToken);
        return payload["request"]?.Deserialize<RunQueueRequest>(AutomationCliJson.Options)
            ?? throw new InvalidOperationException($"Queue request '{requestId}' response did not contain request.");
    }

    private static bool IsTerminal(string status) =>
        string.Equals(status, "Completed", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(status, "Failed", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(status, "Canceled", StringComparison.OrdinalIgnoreCase);

    private async Task<RunQueueRequest> ReadRunQueueRequestAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        return await JsonSerializer.DeserializeAsync<RunQueueRequest>(stream, AutomationCliJson.Options, cancellationToken)
            ?? throw new InvalidOperationException("Run queue response did not contain a request.");
    }

    private async Task<JsonObject> GetJsonObjectAsync(string path, CancellationToken cancellationToken) =>
        await GetJsonObjectAsync(_apiClient, path, cancellationToken);

    private static async Task<JsonObject> GetJsonObjectAsync(HttpClient client, string path, CancellationToken cancellationToken)
    {
        var response = await client.GetAsync(path, cancellationToken);
        await EnsureSuccessAsync(response, $"GET {path}", cancellationToken);
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        return await JsonSerializer.DeserializeAsync<JsonObject>(stream, AutomationCliJson.Options, cancellationToken)
            ?? throw new InvalidOperationException($"GET {path} returned empty JSON.");
    }

    private async Task<IReadOnlyList<JsonObject>> GetRunEntriesAsync(string runId, CancellationToken cancellationToken)
    {
        const int pageSize = 200;
        var page = 1;
        var entries = new List<JsonObject>();
        int? total = null;
        do
        {
            var payload = await GetJsonObjectAsync($"/api/runs/{Uri.EscapeDataString(runId)}/entries?page={page}&pageSize={pageSize}", cancellationToken);
            total ??= payload["total"]?.GetValue<int>() ?? 0;
            entries.AddRange(payload["entries"]?.AsArray()
                .OfType<JsonObject>()
                .Select(CloneObject) ?? []);
            page++;
        }
        while (entries.Count < total);

        return entries;
    }

    private static IReadOnlyList<string> ValidateIteration(AutomationIteration iteration, JsonObject runDetail, IReadOnlyList<JsonObject> entries)
    {
        var failures = new List<string>();
        var expectation = iteration.Expectation!;
        var run = runDetail["run"]?.AsObject();
        var runStatus = ReadString(run, "status");
        if (!string.IsNullOrWhiteSpace(expectation.RunStatus) &&
            !string.Equals(runStatus, expectation.RunStatus, StringComparison.OrdinalIgnoreCase))
        {
            failures.Add($"expected runStatus {expectation.RunStatus} but found {runStatus ?? "(missing)"}.");
        }

        var actualCounts = ExtractBucketCounts(runDetail, entries);
        foreach (var expected in expectation.BucketCounts)
        {
            actualCounts.TryGetValue(expected.Key, out var actual);
            if (actual != expected.Value)
            {
                failures.Add($"expected bucket {expected.Key}={expected.Value} but found {actual}.");
            }
        }

        foreach (var expectedWorker in expectation.WorkerOperations)
        {
            var operations = entries
                .Where(entry => string.Equals(ReadString(entry, "workerId"), expectedWorker.WorkerId, StringComparison.OrdinalIgnoreCase))
                .SelectMany(ExtractOperationNames)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var operation in expectedWorker.Operations)
            {
                if (!operations.Contains(operation))
                {
                    failures.Add($"worker {expectedWorker.WorkerId} missing operation {operation}.");
                }
            }
        }

        return failures;
    }

    private async Task<IReadOnlyList<string>> VerifyAdAsync(AutomationScenario scenario, CancellationToken cancellationToken)
    {
        var expectedUsers = scenario.FinalExpectation?.ExpectedAdUsers ?? scenario.FinalExpectation?.DirectoryUsers ?? [];
        if (expectedUsers.Count == 0)
        {
            await AutomationConsole.WriteWarningAsync(output, $"[{scenario.Name}] No final AD users declared; skipping AD readback.");
            return [];
        }

        var failures = new List<string>();
        var loader = new SyncFactorsConfigurationLoader(new SyncFactorsConfigPathResolver(options.ConfigPath, options.MappingConfigPath));
        var config = loader.GetSyncConfig();
        expectedUsers = ResolveExpectedUsers(expectedUsers, config);
        if (!config.Sync.RealSyncEnabled)
        {
            throw new InvalidOperationException("Automation real AD verification requires sync.realSyncEnabled=true.");
        }

        var mappingProvider = new AttributeMappingProvider(loader, NullLogger<AttributeMappingProvider>.Instance);
        using var pool = new ActiveDirectoryConnectionPool();
        var gateway = new ActiveDirectoryGateway(loader, mappingProvider, pool, NullLogger<ActiveDirectoryGateway>.Instance);

        foreach (var expected in expectedUsers)
        {
            await AutomationConsole.WriteInfoAsync(output, $"[{scenario.Name}] Reading AD user {expected.WorkerId}.");
            var worker = new WorkerSnapshot(
                WorkerId: expected.WorkerId,
                PreferredName: expected.WorkerId,
                LastName: expected.WorkerId,
                Department: "Automation",
                TargetOu: expected.ParentOu ?? config.Ad.DefaultActiveOu,
                IsPrehire: false,
                Attributes: new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
                {
                    [config.Ad.IdentityAttribute] = expected.WorkerId,
                    ["personIdExternal"] = expected.WorkerId,
                    ["userId"] = expected.WorkerId,
                    ["employeeID"] = expected.WorkerId
                });
            var actual = await gateway.FindByWorkerAsync(worker, cancellationToken);
            if (actual is null)
            {
                failures.Add($"Expected AD user {expected.WorkerId} was not found.");
                continue;
            }

            CompareExpectedUser(expected, actual, failures);
            await AutomationConsole.WritePassAsync(output, $"[{scenario.Name}] AD user {expected.WorkerId} found. sam={actual.SamAccountName} enabled={actual.Enabled} dn={actual.DistinguishedName}");
        }

        return failures;
    }

    private async Task WriteFailureDiagnosticsAsync(
        AutomationScenario scenario,
        IReadOnlyList<string> failures,
        AutomationIteration? iteration,
        string? runId)
    {
        if (failures.Count == 0)
        {
            return;
        }

        await AutomationConsole.WriteFailureAsync(output, $"[{scenario.Name}] Failure diagnostics:");
        var distinct = failures
            .SelectMany(failure => AutomationFailureAnalyzer.Analyze(failure, scenario))
            .DistinctBy(diagnosis => $"{diagnosis.LikelyFailure}|{diagnosis.Check}", StringComparer.OrdinalIgnoreCase)
            .Take(8)
            .ToArray();
        foreach (var failure in failures.Take(8))
        {
            await AutomationConsole.WriteFailureAsync(output, $"  failure: {failure}");
        }

        foreach (var diagnosis in distinct)
        {
            await AutomationConsole.WriteActionAsync(output, $"  likely failed: {diagnosis.LikelyFailure}");
            await AutomationConsole.WriteActionAsync(output, $"  check: {diagnosis.Check}");
        }

        if (iteration is not null)
        {
            await AutomationConsole.WriteActionAsync(output, $"  scenario file: {scenario.SourcePath}");
            await AutomationConsole.WriteActionAsync(output, $"  iteration: {iteration.Order} {iteration.Name ?? string.Empty}".TrimEnd());
        }

        if (!string.IsNullOrWhiteSpace(runId))
        {
            await AutomationConsole.WriteActionAsync(output, $"  run detail: {options.ApiUrl.ToString().TrimEnd('/')}/Runs/Detail?runId={Uri.EscapeDataString(runId)}");
        }

        await AutomationConsole.WriteActionAsync(output, $"  report: {options.ReportPath}");
    }

    private static void CompareExpectedUser(AutomationExpectedAdUser expected, DirectoryUserSnapshot actual, List<string> failures)
    {
        if (!string.IsNullOrWhiteSpace(expected.SamAccountName) &&
            !string.Equals(actual.SamAccountName, expected.SamAccountName, StringComparison.OrdinalIgnoreCase))
        {
            failures.Add($"AD user {expected.WorkerId} expected sam {expected.SamAccountName} but found {actual.SamAccountName}.");
        }

        if (!string.IsNullOrWhiteSpace(expected.ParentOu))
        {
            var actualParent = DirectoryDistinguishedName.GetParentOu(actual.DistinguishedName);
            if (!string.Equals(actualParent, expected.ParentOu, StringComparison.OrdinalIgnoreCase))
            {
                failures.Add($"AD user {expected.WorkerId} expected OU {expected.ParentOu} but found {actualParent}.");
            }
        }

        if (expected.Enabled is not null && actual.Enabled != expected.Enabled.Value)
        {
            failures.Add($"AD user {expected.WorkerId} expected enabled={expected.Enabled.Value} but found {actual.Enabled}.");
        }

        if (!string.IsNullOrWhiteSpace(expected.DisplayName) &&
            !string.Equals(actual.DisplayName, expected.DisplayName, StringComparison.Ordinal))
        {
            failures.Add($"AD user {expected.WorkerId} expected displayName '{expected.DisplayName}' but found '{actual.DisplayName}'.");
        }

        foreach (var pair in expected.Attributes)
        {
            actual.Attributes.TryGetValue(pair.Key, out var actualValue);
            if (!string.Equals(actualValue, pair.Value, StringComparison.Ordinal))
            {
                failures.Add($"AD user {expected.WorkerId} expected {pair.Key}='{pair.Value}' but found '{actualValue}'.");
            }
        }
    }

    private static IReadOnlyList<AutomationExpectedAdUser> ResolveExpectedUsers(IReadOnlyList<AutomationExpectedAdUser> expectedUsers, SyncFactorsConfigDocument config)
    {
        return expectedUsers
            .Select(expected => expected with
            {
                ParentOu = ResolveOuToken(expected.ParentOu, config),
                Attributes = expected.Attributes
                    .ToDictionary(pair => pair.Key, pair => ResolveAttributeToken(pair.Value, config), StringComparer.OrdinalIgnoreCase)
            })
            .ToArray();
    }

    private static string? ResolveOuToken(string? value, SyncFactorsConfigDocument config) =>
        value switch
        {
            "{{activeOu}}" => config.Ad.DefaultActiveOu,
            "{{prehireOu}}" => config.Ad.PrehireOu,
            "{{graveyardOu}}" => config.Ad.GraveyardOu,
            "{{leaveOu}}" => string.IsNullOrWhiteSpace(config.Ad.LeaveOu) ? config.Ad.DefaultActiveOu : config.Ad.LeaveOu,
            _ => value
        };

    private static string? ResolveAttributeToken(string? value, SyncFactorsConfigDocument config) =>
        value switch
        {
            "{{activeOu}}" => config.Ad.DefaultActiveOu,
            "{{prehireOu}}" => config.Ad.PrehireOu,
            "{{graveyardOu}}" => config.Ad.GraveyardOu,
            "{{leaveOu}}" => string.IsNullOrWhiteSpace(config.Ad.LeaveOu) ? config.Ad.DefaultActiveOu : config.Ad.LeaveOu,
            _ => value
        };

    private static IReadOnlyDictionary<string, int> ExtractBucketCounts(JsonObject? runDetail, IReadOnlyList<JsonObject> entries)
    {
        var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        if (runDetail?["bucketCounts"] is JsonObject bucketCounts)
        {
            foreach (var pair in bucketCounts)
            {
                counts[pair.Key] = pair.Value?.GetValue<int>() ?? 0;
            }
        }

        foreach (var group in entries.GroupBy(entry => ReadString(entry, "bucket") ?? string.Empty, StringComparer.OrdinalIgnoreCase))
        {
            if (!string.IsNullOrWhiteSpace(group.Key) && !counts.ContainsKey(group.Key))
            {
                counts[group.Key] = group.Count();
            }
        }

        return counts;
    }

    private static IEnumerable<string> ExtractOperationNames(JsonNode node)
    {
        if (node is JsonValue value && value.TryGetValue<string>(out var text) && IsDirectoryOperationName(text))
        {
            yield return text;
        }
        else if (node is JsonObject obj)
        {
            foreach (var child in obj.SelectMany(pair => pair.Value is null ? [] : ExtractOperationNames(pair.Value)))
            {
                yield return child;
            }
        }
        else if (node is JsonArray array)
        {
            foreach (var child in array.OfType<JsonNode>().SelectMany(ExtractOperationNames))
            {
                yield return child;
            }
        }
    }

    private static bool IsDirectoryOperationName(string text) =>
        string.Equals(text, "CreateUser", StringComparison.Ordinal) ||
        string.Equals(text, "UpdateUser", StringComparison.Ordinal) ||
        string.Equals(text, "MoveUser", StringComparison.Ordinal) ||
        string.Equals(text, "EnableUser", StringComparison.Ordinal) ||
        string.Equals(text, "DisableUser", StringComparison.Ordinal) ||
        string.Equals(text, "DeleteUser", StringComparison.Ordinal);

    private static string? ReadString(JsonObject? obj, string name) =>
        obj is not null && obj.TryGetPropertyValue(name, out var value) ? value?.GetValue<string>() : null;

    private static string FormatCounts(IReadOnlyDictionary<string, int> counts) =>
        counts.Count == 0 ? "(none)" : string.Join(", ", counts.OrderBy(pair => pair.Key).Select(pair => $"{pair.Key}={pair.Value}"));

    private static void SetJsonPath(JsonObject obj, string path, string? value)
    {
        var parts = path.Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length == 0)
        {
            throw new InvalidOperationException("Mutation set path cannot be empty.");
        }

        var current = obj;
        foreach (var part in parts[..^1])
        {
            if (current[part] is not JsonObject child)
            {
                child = [];
                current[part] = child;
            }

            current = child;
        }

        current[parts[^1]] = value is null ? null : JsonValue.Create(value);
    }

    private static JsonObject CloneObject(JsonObject obj) =>
        JsonNode.Parse(obj.ToJsonString())!.AsObject();

    private static async Task EnsureSuccessAsync(HttpResponseMessage response, string operation, CancellationToken cancellationToken)
    {
        if (response.IsSuccessStatusCode)
        {
            return;
        }

        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        throw new InvalidOperationException($"{operation} failed with HTTP {(int)response.StatusCode}: {body}");
    }

    public ValueTask DisposeAsync()
    {
        _apiClient.Dispose();
        _mockClient.Dispose();
        return ValueTask.CompletedTask;
    }
}

public static class AutomationReportWriter
{
    public static async Task WriteAsync(AutomationRunReport report, string markdownPath, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(markdownPath)!);
        var jsonPath = Path.ChangeExtension(markdownPath, ".json");
        await File.WriteAllTextAsync(jsonPath, JsonSerializer.Serialize(report, AutomationCliJson.Options), cancellationToken);
        await File.WriteAllTextAsync(markdownPath, BuildMarkdown(report), cancellationToken);
    }

    public static void WriteSummary(TextWriter output, AutomationRunReport report, string markdownPath)
    {
        AutomationConsole.WriteLine(output, $"Automation Result: {(report.Passed ? "PASSED" : "FAILED")}", report.Passed ? AutomationConsoleKind.Pass : AutomationConsoleKind.Failure);
        output.WriteLine($"Scenarios: {report.Scenarios.Count}");
        output.WriteLine($"Markdown Report: {markdownPath}");
        output.WriteLine($"Json Report: {Path.ChangeExtension(markdownPath, ".json")}");
        foreach (var scenario in report.Scenarios.Where(scenario => !scenario.Passed))
        {
            AutomationConsole.WriteLine(output, $"Failed scenario: {scenario.Name}", AutomationConsoleKind.Failure);
            foreach (var diagnosis in BuildScenarioDiagnoses(scenario).Take(4))
            {
                AutomationConsole.WriteLine(output, $"  likely failed: {diagnosis.LikelyFailure}", AutomationConsoleKind.Action);
                AutomationConsole.WriteLine(output, $"  check: {diagnosis.Check}", AutomationConsoleKind.Action);
            }
        }
    }

    private static string BuildMarkdown(AutomationRunReport report)
    {
        var builder = new StringBuilder();
        builder.AppendLine("# SyncFactors E2E Automation Report");
        builder.AppendLine();
        builder.AppendLine($"Result: {(report.Passed ? "PASSED" : "FAILED")}");
        builder.AppendLine($"Started: {report.StartedAtUtc:O}");
        builder.AppendLine($"Completed: {report.CompletedAtUtc:O}");
        builder.AppendLine();
        foreach (var scenario in report.Scenarios)
        {
            builder.AppendLine($"## {scenario.Name}");
            builder.AppendLine($"Source: `{scenario.SourcePath}`");
            builder.AppendLine($"Risk: `{scenario.RiskLevel}`");
            builder.AppendLine($"Result: {(scenario.Passed ? "PASSED" : "FAILED")}");
            if (scenario.Preflight.Count > 0)
            {
                builder.AppendLine("Preflight:");
                foreach (var result in scenario.Preflight)
                {
                    builder.AppendLine($"- {(result.Passed ? "PASS" : "FAIL")} `{result.Name}`: {result.Message}");
                }
            }

            if (scenario.BaselineAdSnapshot is not null || scenario.FinalAdSnapshot is not null)
            {
                builder.AppendLine($"AD snapshot: baseline={scenario.BaselineAdSnapshot?.TotalUsers.ToString() ?? "n/a"} final={scenario.FinalAdSnapshot?.TotalUsers.ToString() ?? "n/a"}");
            }

            foreach (var diff in scenario.Diagnostics.AdDiff)
            {
                builder.AppendLine($"- ad-{diff.Severity.ToLowerInvariant()}: {diff.Message}");
            }

            foreach (var failure in scenario.Failures)
            {
                builder.AppendLine($"- failure: {failure}");
            }

            var diagnoses = BuildScenarioDiagnoses(scenario).ToArray();
            if (diagnoses.Length > 0)
            {
                builder.AppendLine("Failure diagnosis:");
                foreach (var diagnosis in diagnoses)
                {
                    builder.AppendLine($"- likely failed: {diagnosis.LikelyFailure}");
                    builder.AppendLine($"  check: {diagnosis.Check}");
                }
            }

            foreach (var iteration in scenario.Iterations)
            {
                builder.AppendLine($"- {iteration.Order}. {iteration.Name}: {iteration.QueueStatus} run=`{iteration.RunId ?? "(none)"}` buckets={FormatCounts(iteration.BucketCounts)}");
                foreach (var failure in iteration.Failures)
                {
                    builder.AppendLine($"  - failure: {failure}");
                }
            }

            builder.AppendLine();
        }

        return builder.ToString();
    }

    private static string FormatCounts(IReadOnlyDictionary<string, int> counts) =>
        counts.Count == 0 ? "(none)" : string.Join(", ", counts.OrderBy(pair => pair.Key).Select(pair => $"{pair.Key}={pair.Value}"));

    private static IReadOnlyList<AutomationFailureDiagnosis> BuildScenarioDiagnoses(AutomationScenarioReport scenario)
    {
        return scenario.Failures
            .Concat(scenario.Iterations.SelectMany(iteration => iteration.Failures.Select(failure => $"Iteration {iteration.Order}: {failure}")))
            .Concat(scenario.Diagnostics.AdDiff.Select(diff => diff.Message))
            .SelectMany(failure => AutomationFailureAnalyzer.Analyze(failure, scenario.SourcePath))
            .DistinctBy(diagnosis => $"{diagnosis.LikelyFailure}|{diagnosis.Check}", StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }
}

public enum AutomationConsoleKind
{
    Info,
    Stage,
    Pass,
    Warning,
    Failure,
    Action
}

public static class AutomationConsole
{
    private const string Reset = "\u001b[0m";

    public static Task WriteInfoAsync(TextWriter output, string message) =>
        WriteLineAsync(output, message, AutomationConsoleKind.Info);

    public static Task WriteStageAsync(TextWriter output, string message) =>
        WriteLineAsync(output, message, AutomationConsoleKind.Stage);

    public static Task WritePassAsync(TextWriter output, string message) =>
        WriteLineAsync(output, message, AutomationConsoleKind.Pass);

    public static Task WriteWarningAsync(TextWriter output, string message) =>
        WriteLineAsync(output, message, AutomationConsoleKind.Warning);

    public static Task WriteFailureAsync(TextWriter output, string message) =>
        WriteLineAsync(output, message, AutomationConsoleKind.Failure);

    public static Task WriteActionAsync(TextWriter output, string message) =>
        WriteLineAsync(output, message, AutomationConsoleKind.Action);

    public static Task WriteLineAsync(TextWriter output, string message, AutomationConsoleKind kind)
    {
        WriteLine(output, message, kind);
        return Task.CompletedTask;
    }

    public static void WriteLine(TextWriter output, string message, AutomationConsoleKind kind)
    {
        output.WriteLine(Format(message, kind));
    }

    private static string Format(string message, AutomationConsoleKind kind)
    {
        if (!UseColor())
        {
            return message;
        }

        return $"{Color(kind)}{Prefix(kind)}{message}{Reset}";
    }

    private static bool UseColor()
    {
        if (Environment.GetEnvironmentVariable("NO_COLOR") is not null)
        {
            return false;
        }

        return !Console.IsOutputRedirected;
    }

    private static string Prefix(AutomationConsoleKind kind) =>
        kind switch
        {
            AutomationConsoleKind.Pass => "PASS ",
            AutomationConsoleKind.Warning => "WARN ",
            AutomationConsoleKind.Failure => "FAIL ",
            AutomationConsoleKind.Action => "NEXT ",
            AutomationConsoleKind.Stage => "==> ",
            _ => string.Empty
        };

    private static string Color(AutomationConsoleKind kind) =>
        kind switch
        {
            AutomationConsoleKind.Pass => "\u001b[32m",
            AutomationConsoleKind.Warning => "\u001b[33m",
            AutomationConsoleKind.Failure => "\u001b[31m",
            AutomationConsoleKind.Action => "\u001b[36m",
            AutomationConsoleKind.Stage => "\u001b[35m",
            _ => "\u001b[37m"
        };
}

public sealed record AutomationFailureDiagnosis(string LikelyFailure, string Check);

public static class AutomationFailureAnalyzer
{
    public static IReadOnlyList<AutomationFailureDiagnosis> Analyze(string failure, object? scenarioContext)
    {
        var scenarioPath = scenarioContext as string ?? (scenarioContext as AutomationScenario)?.SourcePath;
        var checks = new List<AutomationFailureDiagnosis>();
        var text = failure.ToLowerInvariant();
        if (text.Contains("preflight"))
        {
            Add(checks, "environment dependency preflight", "API/worker/mock/AD availability, then rerun with same command.");
        }

        if (text.Contains("api") || text.Contains("login") || text.Contains("401") || text.Contains("403") || text.Contains("http"))
        {
            Add(checks, "API/auth/local automation login", "API logs, /api/health, SYNCFACTORS_AUTOMATION_USERNAME/PASSWORD, and hybrid local auth bootstrap.");
        }

        if (text.Contains("mock") || text.Contains("successfactors") || text.Contains("/api/admin/workers"))
        {
            Add(checks, "mock SuccessFactors state or mutation", "mock service logs and the worker payload in the scenario file.");
        }

        if (text.Contains("queue") || text.Contains("request") || text.Contains("canceled") || text.Contains("timeout"))
        {
            Add(checks, "run queue or worker processing", "API /api/runs/queue/{requestId}, worker logs, and stale runtime status.");
        }

        if (text.Contains("runstatus") || text.Contains("bucket") || text.Contains("operation") || text.Contains("unchanged"))
        {
            Add(checks, "sync engine planning/execution bucket mismatch", $"run detail entries and expectation block in {FormatScenarioPath(scenarioPath)}.");
        }

        if (text.Contains("ad ") || text.Contains("active directory") || text.Contains("ldap") || text.Contains("managed ou") || text.Contains("expected ou") || text.Contains("enabled"))
        {
            Add(checks, "Active Directory final state mismatch", "managed test OUs, AD bind/search permissions, and finalExpectation expectedAdUsers.");
        }

        if (text.Contains("duplicate") || text.Contains("sam") || text.Contains("upn") || text.Contains("mail"))
        {
            Add(checks, "identity uniqueness or correlation", "sAMAccountName/UPN/mail values in AD snapshot and identity correlation config.");
        }

        if (text.Contains("duration") || text.Contains("timed out"))
        {
            Add(checks, "performance threshold or blocked worker", "worker throughput, expectedDurationSeconds, and queue/runtime timestamps.");
        }

        if (checks.Count == 0)
        {
            Add(checks, "scenario assertion or runtime dependency", $"failure line, report JSON, and scenario file {FormatScenarioPath(scenarioPath)}.");
        }

        return checks;
    }

    private static void Add(ICollection<AutomationFailureDiagnosis> checks, string likelyFailure, string check)
    {
        checks.Add(new AutomationFailureDiagnosis(likelyFailure, check));
    }

    private static string FormatScenarioPath(string? scenarioPath) =>
        string.IsNullOrWhiteSpace(scenarioPath) ? "(unknown scenario)" : scenarioPath;
}

public sealed record AutomationScenario(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("tags")] IReadOnlyList<string> Tags,
    [property: JsonPropertyName("riskLevel")] string RiskLevel,
    [property: JsonPropertyName("preflight")] AutomationPreflightOptions? Preflight,
    [property: JsonPropertyName("resetAdBeforeScenario")] bool ResetAdBeforeScenario,
    [property: JsonPropertyName("resetMockBeforeScenario")] bool ResetMockBeforeScenario,
    [property: JsonPropertyName("syncMode")] string SyncMode,
    [property: JsonPropertyName("expectedDurationSeconds")] int? ExpectedDurationSeconds,
    [property: JsonPropertyName("expectedRunStatus")] string? ExpectedRunStatus,
    [property: JsonPropertyName("expectedQueueStatus")] string? ExpectedQueueStatus,
    [property: JsonPropertyName("expectedHealth")] string? ExpectedHealth,
    [property: JsonPropertyName("expectedAuditEvents")] IReadOnlyList<string> ExpectedAuditEvents,
    [property: JsonPropertyName("iterations")] IReadOnlyList<AutomationIteration> Iterations,
    [property: JsonPropertyName("finalExpectation")] AutomationFinalExpectation? FinalExpectation)
{
    public string SourcePath { get; init; } = string.Empty;
}

public sealed record AutomationIteration(
    [property: JsonPropertyName("order")] int Order,
    [property: JsonPropertyName("name")] string? Name,
    [property: JsonPropertyName("mutations")] IReadOnlyList<AutomationWorkerMutation> Mutations,
    [property: JsonPropertyName("expectation")] AutomationIterationExpectation? Expectation,
    [property: JsonPropertyName("sourceAssertions")] JsonObject? SourceAssertions,
    [property: JsonPropertyName("runAssertions")] JsonObject? RunAssertions,
    [property: JsonPropertyName("adAssertions")] JsonObject? AdAssertions,
    [property: JsonPropertyName("reportAssertions")] JsonObject? ReportAssertions);

public sealed record AutomationPreflightOptions(
    [property: JsonPropertyName("apiHealth")] bool ApiHealth = true,
    [property: JsonPropertyName("mockHealth")] bool MockHealth = true,
    [property: JsonPropertyName("workerHeartbeat")] bool WorkerHeartbeat = true,
    [property: JsonPropertyName("activeDirectory")] bool ActiveDirectory = true,
    [property: JsonPropertyName("managedOuSnapshot")] bool ManagedOuSnapshot = true);

public sealed record AutomationWorkerMutation(
    [property: JsonPropertyName("workerId")] string WorkerId,
    [property: JsonPropertyName("removeFromSource")] bool RemoveFromSource,
    [property: JsonPropertyName("worker")] JsonObject? Worker,
    [property: JsonPropertyName("set")] IReadOnlyDictionary<string, string?> Set);

public sealed record AutomationIterationExpectation(
    [property: JsonPropertyName("runStatus")] string? RunStatus,
    [property: JsonPropertyName("bucketCounts")] IReadOnlyDictionary<string, int> BucketCounts,
    [property: JsonPropertyName("workerOperations")] IReadOnlyList<AutomationExpectedWorkerOperation> WorkerOperations);

public sealed record AutomationExpectedWorkerOperation(
    [property: JsonPropertyName("workerId")] string WorkerId,
    [property: JsonPropertyName("operations")] IReadOnlyList<string> Operations);

public sealed record AutomationFinalExpectation(
    [property: JsonPropertyName("expectedAdUsers")] IReadOnlyList<AutomationExpectedAdUser>? ExpectedAdUsers,
    [property: JsonPropertyName("directoryUsers")] IReadOnlyList<AutomationExpectedAdUser>? DirectoryUsers);

public sealed record AutomationExpectedAdUser(
    [property: JsonPropertyName("workerId")] string WorkerId,
    [property: JsonPropertyName("samAccountName")] string? SamAccountName,
    [property: JsonPropertyName("parentOu")] string? ParentOu,
    [property: JsonPropertyName("enabled")] bool? Enabled,
    [property: JsonPropertyName("displayName")] string? DisplayName,
    [property: JsonPropertyName("attributes")] IReadOnlyDictionary<string, string?> Attributes);

public sealed record AutomationRunReport(
    DateTimeOffset StartedAtUtc,
    DateTimeOffset CompletedAtUtc,
    bool Passed,
    IReadOnlyList<AutomationScenarioReport> Scenarios);

public sealed record AutomationScenarioReport(
    string Name,
    string SourcePath,
    string RiskLevel,
    DateTimeOffset StartedAtUtc,
    DateTimeOffset CompletedAtUtc,
    bool Passed,
    IReadOnlyList<string> Failures,
    IReadOnlyList<AutomationPreflightResult> Preflight,
    AutomationAdSnapshot? BaselineAdSnapshot,
    AutomationAdSnapshot? FinalAdSnapshot,
    IReadOnlyList<AutomationIterationReport> Iterations,
    AutomationScenarioDiagnostics Diagnostics);

public sealed record AutomationIterationReport(
    int Order,
    string Name,
    string RequestId,
    string? RunId,
    string QueueStatus,
    IReadOnlyDictionary<string, int> BucketCounts,
    IReadOnlyList<string> Failures);

public sealed record AutomationPreflightResult(
    string Name,
    bool Passed,
    string Message,
    DateTimeOffset CheckedAtUtc);

public sealed record AutomationScenarioDiagnostics(
    string? LastQueueStatus,
    string? LastRunId,
    string? LastWorkerHeartbeat,
    IReadOnlyList<AutomationAdDiffItem> AdDiff);

public sealed record AutomationAdSnapshot(
    string Phase,
    DateTimeOffset CapturedAtUtc,
    int TotalUsers,
    IReadOnlyList<AutomationAdSnapshotUser> Users,
    IReadOnlyList<AutomationAdDiffItem> Warnings);

public sealed record AutomationAdSnapshotUser(
    string OuName,
    string ParentOu,
    string? WorkerId,
    string? SamAccountName,
    string? DistinguishedName,
    bool? Enabled,
    string? DisplayName,
    IReadOnlyDictionary<string, string?> Attributes)
{
    public static AutomationAdSnapshotUser FromDirectoryUser(string ouName, string parentOu, DirectoryUserSnapshot user, string identityAttribute)
    {
        user.Attributes.TryGetValue(identityAttribute, out var workerId);
        if (string.IsNullOrWhiteSpace(workerId))
        {
            user.Attributes.TryGetValue("employeeID", out workerId);
        }

        return new AutomationAdSnapshotUser(
            ouName,
            parentOu,
            workerId,
            user.SamAccountName,
            user.DistinguishedName,
            user.Enabled,
            user.DisplayName,
            user.Attributes);
    }
}

public sealed record AutomationAdDiffItem(string Severity, string Message, string? WorkerId = null);

public static class AutomationAdDiff
{
    public static IReadOnlyList<AutomationAdDiffItem> Build(
        IReadOnlyList<AutomationExpectedAdUser> expectedUsers,
        AutomationAdSnapshot? actualSnapshot)
    {
        if (actualSnapshot is null)
        {
            return expectedUsers.Count == 0
                ? []
                : [new AutomationAdDiffItem("Failure", "AD snapshot was not captured, so expected AD users could not be verified.")];
        }

        var diffs = new List<AutomationAdDiffItem>();
        var expectedByWorker = expectedUsers.ToDictionary(user => user.WorkerId, StringComparer.OrdinalIgnoreCase);
        var actualByWorker = actualSnapshot.Users
            .Where(user => !string.IsNullOrWhiteSpace(user.WorkerId))
            .GroupBy(user => user.WorkerId!, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.ToArray(), StringComparer.OrdinalIgnoreCase);

        foreach (var expected in expectedUsers)
        {
            if (!actualByWorker.TryGetValue(expected.WorkerId, out var matches) || matches.Length == 0)
            {
                diffs.Add(new AutomationAdDiffItem("Failure", $"Expected AD user {expected.WorkerId} was missing from managed OU snapshot.", expected.WorkerId));
                continue;
            }

            if (matches.Length > 1)
            {
                diffs.Add(new AutomationAdDiffItem("Failure", $"Expected AD user {expected.WorkerId} matched {matches.Length} managed OU objects.", expected.WorkerId));
            }

            var actual = matches[0];
            AddMismatch(diffs, expected.WorkerId, "sam", expected.SamAccountName, actual.SamAccountName);
            AddMismatch(diffs, expected.WorkerId, "parent OU", expected.ParentOu, actual.ParentOu, ignoreCase: true);
            if (expected.Enabled is not null && actual.Enabled != expected.Enabled)
            {
                diffs.Add(new AutomationAdDiffItem("Failure", $"AD user {expected.WorkerId} expected enabled={expected.Enabled} but found {actual.Enabled}.", expected.WorkerId));
            }

            AddMismatch(diffs, expected.WorkerId, "displayName", expected.DisplayName, actual.DisplayName);
            foreach (var attribute in expected.Attributes)
            {
                actual.Attributes.TryGetValue(attribute.Key, out var actualValue);
                AddMismatch(diffs, expected.WorkerId, attribute.Key, attribute.Value, actualValue);
            }
        }

        foreach (var unexpected in actualSnapshot.Users.Where(user => !string.IsNullOrWhiteSpace(user.WorkerId) && !expectedByWorker.ContainsKey(user.WorkerId!)))
        {
            diffs.Add(new AutomationAdDiffItem(
                "Failure",
                $"Unexpected AD user {unexpected.WorkerId} remained in managed OU snapshot: sam={unexpected.SamAccountName}, ou={unexpected.ParentOu}.",
                unexpected.WorkerId));
        }

        diffs.AddRange(FindDuplicateDirectoryValues(actualSnapshot.Users));
        return diffs;
    }

    public static IReadOnlyList<AutomationAdDiffItem> FindDuplicateDirectoryValues(IReadOnlyList<AutomationAdSnapshotUser> users)
    {
        var diffs = new List<AutomationAdDiffItem>();
        AddDuplicateValueDiffs(users, "sAMAccountName", user => user.SamAccountName, diffs);
        AddDuplicateValueDiffs(users, "userPrincipalName", user => ReadAttribute(user, "UserPrincipalName"), diffs);
        AddDuplicateValueDiffs(users, "mail", user => ReadAttribute(user, "mail"), diffs);
        return diffs;
    }

    private static void AddMismatch(
        ICollection<AutomationAdDiffItem> diffs,
        string workerId,
        string field,
        string? expected,
        string? actual,
        bool ignoreCase = false)
    {
        if (string.IsNullOrWhiteSpace(expected))
        {
            return;
        }

        var comparison = ignoreCase ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (!string.Equals(expected, actual, comparison))
        {
            diffs.Add(new AutomationAdDiffItem("Failure", $"AD user {workerId} expected {field} '{expected}' but found '{actual}'.", workerId));
        }
    }

    private static void AddDuplicateValueDiffs(
        IReadOnlyList<AutomationAdSnapshotUser> users,
        string field,
        Func<AutomationAdSnapshotUser, string?> selector,
        ICollection<AutomationAdDiffItem> diffs)
    {
        foreach (var duplicate in users
            .Select(user => new { User = user, Value = selector(user) })
            .Where(item => !string.IsNullOrWhiteSpace(item.Value))
            .GroupBy(item => item.Value!, StringComparer.OrdinalIgnoreCase)
            .Where(group => group.Count() > 1))
        {
            var workers = string.Join(", ", duplicate.Select(item => item.User.WorkerId ?? item.User.SamAccountName ?? item.User.DistinguishedName));
            diffs.Add(new AutomationAdDiffItem("Failure", $"Duplicate AD {field} '{duplicate.Key}' found for {workers}."));
        }
    }

    private static string? ReadAttribute(AutomationAdSnapshotUser user, string name) =>
        user.Attributes.TryGetValue(name, out var value) ? value : null;
}

internal static class AutomationCliJson
{
    public static JsonSerializerOptions Options { get; } = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    };
}

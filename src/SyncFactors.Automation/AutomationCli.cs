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

            await using var runner = new AutomationRunner(options, output);
            var report = await runner.RunAsync(scenarios, cancellationToken);
            await AutomationReportWriter.WriteAsync(report, options.ReportPath, cancellationToken);
            AutomationReportWriter.WriteSummary(output, report, options.ReportPath);
            return report.Passed ? 0 : 1;
        }
        catch (Exception ex)
        {
            await output.WriteLineAsync($"Automation failed: {ex.Message}");
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
    TimeSpan Timeout)
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
            Timeout: TimeSpan.FromMinutes(timeoutMinutes));
    }

    private static bool IsFlag(string key) => string.Equals(key, "allow-ad-reset", StringComparison.OrdinalIgnoreCase);

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
                Iterations = loaded.Iterations ?? []
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
        ValidateLocalConfigurationSafety();
        await AuthenticateAsync(cancellationToken);

        var scenarioReports = new List<AutomationScenarioReport>();
        foreach (var scenario in scenarios)
        {
            await output.WriteLineAsync($"Running scenario: {scenario.Name}");
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

        if (scenario.ResetMockBeforeScenario)
        {
            await EnsureSuccessAsync(await _mockClient.PostAsync("/api/admin/reset", null, cancellationToken), "mock reset", cancellationToken);
        }

        if (scenario.ResetAdBeforeScenario)
        {
            if (!options.AllowAdReset)
            {
                throw new InvalidOperationException($"Scenario '{scenario.Name}' requires AD reset. Re-run with --allow-ad-reset after confirming the configured AD OUs are test-only.");
            }

            var reset = await QueueAndWaitAsync("/api/runs/delete-all", new { confirmationText = "DELETE ALL USERS" }, cancellationToken);
            if (!string.Equals(reset.Status, "Completed", StringComparison.OrdinalIgnoreCase))
            {
                failures.Add($"AD reset failed: {reset.ErrorMessage ?? reset.Status}");
            }
        }

        foreach (var iteration in scenario.Iterations.OrderBy(iteration => iteration.Order))
        {
            await ApplyMutationsAsync(iteration.Mutations ?? [], cancellationToken);
            var queued = await QueueAndWaitAsync(
                "/api/runs",
                new { dryRun = false, mode = scenario.SyncMode, runTrigger = "Automation", requestedBy = "Automation" },
                cancellationToken);
            var iterationFailures = new List<string>();
            if (!string.Equals(queued.Status, "Completed", StringComparison.OrdinalIgnoreCase) || string.IsNullOrWhiteSpace(queued.RunId))
            {
                iterationFailures.Add($"Run queue request {queued.RequestId} ended with status {queued.Status}: {queued.ErrorMessage}");
            }

            JsonObject? runDetail = null;
            IReadOnlyList<JsonObject> entries = [];
            if (!string.IsNullOrWhiteSpace(queued.RunId))
            {
                runDetail = await GetJsonObjectAsync($"/api/runs/{queued.RunId}", cancellationToken);
                entries = await GetRunEntriesAsync(queued.RunId, cancellationToken);
                iterationFailures.AddRange(ValidateIteration(iteration, runDetail, entries));
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

        failures.AddRange(await VerifyAdAsync(scenario, cancellationToken));
        return new AutomationScenarioReport(
            Name: scenario.Name,
            SourcePath: scenario.SourcePath,
            StartedAtUtc: startedAt,
            CompletedAtUtc: DateTimeOffset.UtcNow,
            Passed: failures.Count == 0,
            Failures: failures,
            Iterations: iterationReports);
    }

    private async Task ApplyMutationsAsync(IReadOnlyList<AutomationWorkerMutation> mutations, CancellationToken cancellationToken)
    {
        foreach (var mutation in mutations)
        {
            if (mutation.RemoveFromSource)
            {
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

            var response = exists
                ? await _mockClient.PutAsJsonAsync($"/api/admin/workers/{Uri.EscapeDataString(mutation.WorkerId)}", worker, cancellationToken)
                : await _mockClient.PostAsJsonAsync("/api/admin/workers", worker, cancellationToken);
            await EnsureSuccessAsync(response, $"upsert mock worker {mutation.WorkerId}", cancellationToken);
        }
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
        var deadline = DateTimeOffset.UtcNow.Add(options.Timeout);
        while (DateTimeOffset.UtcNow < deadline)
        {
            var latest = await GetQueueRequestAsync(queued.RequestId, cancellationToken);
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
            return [];
        }

        var failures = new List<string>();
        var loader = new SyncFactorsConfigurationLoader(new SyncFactorsConfigPathResolver(options.ConfigPath, options.MappingConfigPath));
        var config = loader.GetSyncConfig();
        if (!config.Sync.RealSyncEnabled)
        {
            throw new InvalidOperationException("Automation real AD verification requires sync.realSyncEnabled=true.");
        }

        var mappingProvider = new AttributeMappingProvider(loader, NullLogger<AttributeMappingProvider>.Instance);
        using var pool = new ActiveDirectoryConnectionPool();
        var gateway = new ActiveDirectoryGateway(loader, mappingProvider, pool, NullLogger<ActiveDirectoryGateway>.Instance);

        foreach (var expected in expectedUsers)
        {
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
        }

        return failures;
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
        output.WriteLine($"Automation Result: {(report.Passed ? "PASSED" : "FAILED")}");
        output.WriteLine($"Scenarios: {report.Scenarios.Count}");
        output.WriteLine($"Markdown Report: {markdownPath}");
        output.WriteLine($"Json Report: {Path.ChangeExtension(markdownPath, ".json")}");
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
            builder.AppendLine($"Result: {(scenario.Passed ? "PASSED" : "FAILED")}");
            foreach (var failure in scenario.Failures)
            {
                builder.AppendLine($"- failure: {failure}");
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
}

public sealed record AutomationScenario(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("tags")] IReadOnlyList<string> Tags,
    [property: JsonPropertyName("resetAdBeforeScenario")] bool ResetAdBeforeScenario,
    [property: JsonPropertyName("resetMockBeforeScenario")] bool ResetMockBeforeScenario,
    [property: JsonPropertyName("syncMode")] string SyncMode,
    [property: JsonPropertyName("iterations")] IReadOnlyList<AutomationIteration> Iterations,
    [property: JsonPropertyName("finalExpectation")] AutomationFinalExpectation? FinalExpectation)
{
    public string SourcePath { get; init; } = string.Empty;
}

public sealed record AutomationIteration(
    [property: JsonPropertyName("order")] int Order,
    [property: JsonPropertyName("name")] string? Name,
    [property: JsonPropertyName("mutations")] IReadOnlyList<AutomationWorkerMutation> Mutations,
    [property: JsonPropertyName("expectation")] AutomationIterationExpectation? Expectation);

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
    DateTimeOffset StartedAtUtc,
    DateTimeOffset CompletedAtUtc,
    bool Passed,
    IReadOnlyList<string> Failures,
    IReadOnlyList<AutomationIterationReport> Iterations);

public sealed record AutomationIterationReport(
    int Order,
    string Name,
    string RequestId,
    string? RunId,
    string QueueStatus,
    IReadOnlyDictionary<string, int> BucketCounts,
    IReadOnlyList<string> Failures);

internal static class AutomationCliJson
{
    public static JsonSerializerOptions Options { get; } = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    };
}

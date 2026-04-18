using System.Text.Json;

namespace SyncFactors.MockSuccessFactors;

public static class LifecycleSimulationCommand
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true
    };

    public static LifecycleSimulationRequest? TryParse(string[] args)
    {
        if (args.Length == 0 || !string.Equals(args[0], "simulate-lifecycle", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        string? scenario = null;
        string? fixturePath = null;
        string? reportPath = null;
        int? iterations = null;

        for (var index = 1; index < args.Length; index += 2)
        {
            if (index + 1 >= args.Length)
            {
                throw new InvalidOperationException("Expected a value after command argument.");
            }

            var key = args[index];
            var value = args[index + 1];
            switch (key)
            {
                case "--scenario":
                    scenario = value;
                    break;
                case "--fixtures":
                    fixturePath = value;
                    break;
                case "--iterations":
                    if (!int.TryParse(value, out var parsedIterations) || parsedIterations <= 0)
                    {
                        throw new InvalidOperationException("Iterations must be a positive integer.");
                    }

                    iterations = parsedIterations;
                    break;
                case "--report":
                    reportPath = value;
                    break;
                default:
                    throw new InvalidOperationException($"Unsupported argument '{key}'.");
            }
        }

        if (string.IsNullOrWhiteSpace(scenario))
        {
            throw new InvalidOperationException("Usage: simulate-lifecycle --scenario <path> [--fixtures <path>] [--iterations <n>] [--report <path>]");
        }

        return new LifecycleSimulationRequest(
            ScenarioPath: Path.GetFullPath(scenario),
            FixturePath: Path.GetFullPath(fixturePath ?? ResolveDefaultFixturePath()),
            Iterations: iterations,
            ReportPath: string.IsNullOrWhiteSpace(reportPath) ? null : Path.GetFullPath(reportPath));
    }

    public static async Task<int> RunAsync(LifecycleSimulationRequest command, TextWriter output, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(output);

        try
        {
            var scenario = await LoadScenarioAsync(command.ScenarioPath, cancellationToken);
            var fixtures = await LoadFixturesAsync(command.FixturePath, cancellationToken);
            var harness = new LifecycleSimulationHarness(command, scenario, fixtures);
            var report = await harness.RunAsync(cancellationToken);
            string? jsonReportPath = null;
            string? markdownReportPath = null;

            if (!string.IsNullOrWhiteSpace(command.ReportPath))
            {
                (jsonReportPath, markdownReportPath) = await WriteReportsAsync(report, command.ReportPath, cancellationToken);
            }

            await WriteSummaryAsync(output, report, jsonReportPath, markdownReportPath, cancellationToken);
            return report.Passed ? 0 : 1;
        }
        catch (Exception ex)
        {
            await output.WriteLineAsync($"Lifecycle simulation failed: {ex.Message}");
            return 1;
        }
    }

    private static async Task<LifecycleSimulationScenario> LoadScenarioAsync(string path, CancellationToken cancellationToken)
    {
        if (!File.Exists(path))
        {
            throw new FileNotFoundException($"Scenario file was not found at '{path}'.", path);
        }

        var json = await File.ReadAllTextAsync(path, cancellationToken);
        return JsonSerializer.Deserialize<LifecycleSimulationScenario>(json, JsonOptions)
            ?? throw new InvalidOperationException($"Scenario file '{path}' did not deserialize into a valid document.");
    }

    private static async Task<MockFixtureDocument> LoadFixturesAsync(string path, CancellationToken cancellationToken)
    {
        if (!File.Exists(path))
        {
            throw new FileNotFoundException($"Fixture file was not found at '{path}'.", path);
        }

        var json = await File.ReadAllTextAsync(path, cancellationToken);
        return JsonSerializer.Deserialize<MockFixtureDocument>(json, JsonOptions)
            ?? new MockFixtureDocument([]);
    }

    private static async Task<(string? JsonReportPath, string? MarkdownReportPath)> WriteReportsAsync(
        LifecycleSimulationReport report,
        string requestedPath,
        CancellationToken cancellationToken)
    {
        var extension = Path.GetExtension(requestedPath);
        var requestedFullPath = Path.GetFullPath(requestedPath);
        string jsonPath;
        string markdownPath;

        if (string.Equals(extension, ".md", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(extension, ".txt", StringComparison.OrdinalIgnoreCase))
        {
            markdownPath = requestedFullPath;
            jsonPath = Path.ChangeExtension(requestedFullPath, ".json");
        }
        else
        {
            jsonPath = requestedFullPath;
            markdownPath = Path.ChangeExtension(requestedFullPath, ".md");
        }

        Directory.CreateDirectory(Path.GetDirectoryName(jsonPath)!);
        Directory.CreateDirectory(Path.GetDirectoryName(markdownPath)!);

        await File.WriteAllTextAsync(
            jsonPath,
            JsonSerializer.Serialize(report, JsonOptions),
            cancellationToken);
        await File.WriteAllTextAsync(
            markdownPath,
            BuildMarkdownReport(report),
            cancellationToken);

        return (jsonPath, markdownPath);
    }

    private static async Task WriteSummaryAsync(
        TextWriter output,
        LifecycleSimulationReport report,
        string? jsonReportPath,
        string? markdownReportPath,
        CancellationToken cancellationToken)
    {
        var resultLabel = report.Passed ? "PASSED" : "FAILED";
        var duration = report.CompletedAtUtc - report.StartedAtUtc;

        await output.WriteLineAsync("Lifecycle Simulation Report");
        await output.WriteLineAsync($"Scenario: {report.ScenarioName}");
        await output.WriteLineAsync($"Result: {resultLabel}");
        await output.WriteLineAsync($"Duration: {duration.TotalSeconds:F1}s");
        await output.WriteLineAsync($"Fixture Path: {report.FixturePath}");
        await output.WriteLineAsync($"Iterations: {report.Iterations.Count}");
        await output.WriteLineAsync(
            $"Final Directory Users: total={report.FinalDirectoryTotals.TotalUsers}, enabled={report.FinalDirectoryTotals.EnabledUsers}, disabled={report.FinalDirectoryTotals.DisabledUsers}");
        await output.WriteLineAsync(
            $"Aggregate Buckets: {FormatCounts(report.AggregateBucketCounts)}");

        if (!string.IsNullOrWhiteSpace(markdownReportPath))
        {
            await output.WriteLineAsync($"Markdown Report: {markdownReportPath}");
        }

        if (!string.IsNullOrWhiteSpace(jsonReportPath))
        {
            await output.WriteLineAsync($"Json Report: {jsonReportPath}");
        }

        await output.WriteLineAsync(string.Empty);
        await output.WriteLineAsync("Iterations");

        foreach (var iteration in report.Iterations.OrderBy(iteration => iteration.Order))
        {
            cancellationToken.ThrowIfCancellationRequested();
            await output.WriteLineAsync(
                $"{iteration.Order}. {iteration.Name} [{iteration.RunStatus}] buckets: {FormatCounts(iteration.BucketCounts)}");
            foreach (var worker in iteration.WorkerOperations.OrderBy(worker => worker.WorkerId, StringComparer.OrdinalIgnoreCase))
            {
                await output.WriteLineAsync($"   {worker.WorkerId}: {string.Join(", ", worker.Operations)}");
            }

            foreach (var failure in iteration.Failures)
            {
                await output.WriteLineAsync($"   failure: {failure}");
            }
        }

        await output.WriteLineAsync(string.Empty);
        await output.WriteLineAsync("Final Directory State");
        foreach (var user in report.FinalDirectoryUsers.OrderBy(user => user.WorkerId, StringComparer.OrdinalIgnoreCase))
        {
            await output.WriteLineAsync(
                $"{user.WorkerId}: sam={user.SamAccountName} ou={user.ParentOu} enabled={user.Enabled} displayName={user.DisplayName ?? "(unset)"}");
        }

        if (report.Failures.Count > 0)
        {
            await output.WriteLineAsync(string.Empty);
            await output.WriteLineAsync("Failures");
            foreach (var failure in report.Failures)
            {
                await output.WriteLineAsync($"- {failure}");
            }
        }
    }

    private static string BuildMarkdownReport(LifecycleSimulationReport report)
    {
        var lines = new List<string>
        {
            "# Lifecycle Simulation Report",
            string.Empty,
            $"- Scenario: `{report.ScenarioName}`",
            $"- Result: **{(report.Passed ? "PASSED" : "FAILED")}**",
            $"- Duration: `{(report.CompletedAtUtc - report.StartedAtUtc).TotalSeconds:F1}s`",
            $"- Fixture Path: `{report.FixturePath}`",
            $"- Iterations: `{report.Iterations.Count}`",
            $"- Final Directory Users: `{report.FinalDirectoryTotals.TotalUsers}` total, `{report.FinalDirectoryTotals.EnabledUsers}` enabled, `{report.FinalDirectoryTotals.DisabledUsers}` disabled",
            $"- Aggregate Buckets: `{FormatCounts(report.AggregateBucketCounts)}`",
            string.Empty,
            "## Iterations",
            string.Empty
        };

        foreach (var iteration in report.Iterations.OrderBy(iteration => iteration.Order))
        {
            lines.Add($"### {iteration.Order}. {iteration.Name}");
            lines.Add(string.Empty);
            lines.Add($"- Status: `{iteration.RunStatus}`");
            lines.Add($"- Run ID: `{iteration.RunId}`");
            lines.Add($"- Buckets: `{FormatCounts(iteration.BucketCounts)}`");
            if (iteration.WorkerOperations.Count > 0)
            {
                lines.Add("- Worker Operations:");
                foreach (var worker in iteration.WorkerOperations.OrderBy(worker => worker.WorkerId, StringComparer.OrdinalIgnoreCase))
                {
                    lines.Add($"  - `{worker.WorkerId}`: `{string.Join(", ", worker.Operations)}`");
                }
            }

            if (iteration.Failures.Count > 0)
            {
                lines.Add("- Failures:");
                foreach (var failure in iteration.Failures)
                {
                    lines.Add($"  - {failure}");
                }
            }

            lines.Add(string.Empty);
        }

        lines.Add("## Final Directory State");
        lines.Add(string.Empty);
        lines.Add("| Worker | SAM | Parent OU | Enabled | Display Name |");
        lines.Add("| --- | --- | --- | --- | --- |");
        foreach (var user in report.FinalDirectoryUsers.OrderBy(user => user.WorkerId, StringComparer.OrdinalIgnoreCase))
        {
            lines.Add($"| {user.WorkerId} | {user.SamAccountName} | {user.ParentOu} | {user.Enabled} | {user.DisplayName ?? "(unset)"} |");
        }

        if (report.Failures.Count > 0)
        {
            lines.Add(string.Empty);
            lines.Add("## Failures");
            lines.Add(string.Empty);
            foreach (var failure in report.Failures)
            {
                lines.Add($"- {failure}");
            }
        }

        lines.Add(string.Empty);
        return string.Join(Environment.NewLine, lines);
    }

    private static string FormatCounts(IReadOnlyDictionary<string, int> counts)
    {
        if (counts.Count == 0)
        {
            return "none";
        }

        var nonZeroCounts = counts
            .Where(pair => pair.Value > 0)
            .OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase)
            .Select(pair => $"{pair.Key}={pair.Value}")
            .ToArray();

        return nonZeroCounts.Length == 0 ? "none" : string.Join(", ", nonZeroCounts);
    }

    internal static string ResolveDefaultFixturePath()
    {
        var outputContentPath = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory,
            "config",
            "mock-successfactors",
            "baseline-fixtures.json"));
        if (File.Exists(outputContentPath))
        {
            return outputContentPath;
        }

        return Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory,
            "..",
            "..",
            "..",
            "..",
            "..",
            "config",
            "mock-successfactors",
            "baseline-fixtures.json"));
    }
}

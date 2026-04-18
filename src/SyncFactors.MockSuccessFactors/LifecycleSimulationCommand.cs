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

            if (!string.IsNullOrWhiteSpace(command.ReportPath))
            {
                Directory.CreateDirectory(Path.GetDirectoryName(command.ReportPath)!);
                await File.WriteAllTextAsync(
                    command.ReportPath,
                    JsonSerializer.Serialize(report, JsonOptions),
                    cancellationToken);
            }

            await WriteSummaryAsync(output, report, cancellationToken);
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

    private static async Task WriteSummaryAsync(TextWriter output, LifecycleSimulationReport report, CancellationToken cancellationToken)
    {
        await output.WriteLineAsync($"Lifecycle simulation ({report.ScenarioName})");
        await output.WriteLineAsync($"fixturePath={report.FixturePath}");
        await output.WriteLineAsync($"passed={report.Passed}");

        foreach (var iteration in report.Iterations)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var bucketCounts = iteration.BucketCounts.Count == 0
                ? "none"
                : string.Join(", ", iteration.BucketCounts.OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase).Select(pair => $"{pair.Key}={pair.Value}"));
            await output.WriteLineAsync($"iteration[{iteration.Order}] {iteration.Name} status={iteration.RunStatus} buckets={bucketCounts}");
            foreach (var failure in iteration.Failures)
            {
                await output.WriteLineAsync($"  failure: {failure}");
            }
        }

        foreach (var failure in report.Failures)
        {
            await output.WriteLineAsync($"failure: {failure}");
        }

        if (!string.IsNullOrWhiteSpace(report.FinalDirectoryUsers.FirstOrDefault()?.WorkerId))
        {
            await output.WriteLineAsync($"finalDirectoryUsers={report.FinalDirectoryUsers.Count}");
        }
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

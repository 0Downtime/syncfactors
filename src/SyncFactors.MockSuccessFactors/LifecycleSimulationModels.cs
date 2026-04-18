using System.Text.Json.Serialization;

namespace SyncFactors.MockSuccessFactors;

public sealed record LifecycleSimulationRequest(
    string ScenarioPath,
    string FixturePath,
    int? Iterations,
    string? ReportPath);

internal sealed record LifecycleSimulationScenario(
    [property: JsonPropertyName("name")] string? Name,
    [property: JsonPropertyName("initialDirectoryUsers")] IReadOnlyList<LifecycleSimulationDirectoryUserInput>? InitialDirectoryUsers,
    [property: JsonPropertyName("iterations")] IReadOnlyList<LifecycleSimulationIteration>? Iterations,
    [property: JsonPropertyName("finalExpectation")] FinalDirectoryExpectation? FinalExpectation);

internal sealed record LifecycleSimulationIteration(
    [property: JsonPropertyName("order")] int Order,
    [property: JsonPropertyName("name")] string? Name,
    [property: JsonPropertyName("mutations")] IReadOnlyList<WorkerMutation>? Mutations,
    [property: JsonPropertyName("expectation")] IterationExpectation? Expectation);

internal sealed record WorkerMutation(
    [property: JsonPropertyName("workerId")] string WorkerId,
    [property: JsonPropertyName("removeFromSource")] bool RemoveFromSource,
    [property: JsonPropertyName("worker")] MockWorkerFixture? Worker,
    [property: JsonPropertyName("set")] IReadOnlyDictionary<string, string?>? Set);

internal sealed record IterationExpectation(
    [property: JsonPropertyName("runStatus")] string? RunStatus,
    [property: JsonPropertyName("bucketCounts")] IReadOnlyDictionary<string, int>? BucketCounts,
    [property: JsonPropertyName("workerOperations")] IReadOnlyList<ExpectedWorkerOperation>? WorkerOperations);

internal sealed record ExpectedWorkerOperation(
    [property: JsonPropertyName("workerId")] string WorkerId,
    [property: JsonPropertyName("operations")] IReadOnlyList<string>? Operations);

internal sealed record FinalDirectoryExpectation(
    [property: JsonPropertyName("requireExactUserSet")] bool RequireExactUserSet = true,
    [property: JsonPropertyName("directoryUsers")] IReadOnlyList<ExpectedDirectoryUser>? DirectoryUsers = null);

internal sealed record ExpectedDirectoryUser(
    [property: JsonPropertyName("workerId")] string WorkerId,
    [property: JsonPropertyName("samAccountName")] string? SamAccountName,
    [property: JsonPropertyName("parentOu")] string? ParentOu,
    [property: JsonPropertyName("enabled")] bool? Enabled,
    [property: JsonPropertyName("displayName")] string? DisplayName,
    [property: JsonPropertyName("attributes")] IReadOnlyDictionary<string, string?>? Attributes);

internal sealed record LifecycleSimulationDirectoryUserInput(
    [property: JsonPropertyName("workerId")] string WorkerId,
    [property: JsonPropertyName("samAccountName")] string SamAccountName,
    [property: JsonPropertyName("parentOu")] string ParentOu,
    [property: JsonPropertyName("enabled")] bool Enabled,
    [property: JsonPropertyName("displayName")] string? DisplayName,
    [property: JsonPropertyName("attributes")] IReadOnlyDictionary<string, string?>? Attributes);

internal sealed record LifecycleSimulationReport(
    string ScenarioName,
    string FixturePath,
    bool Passed,
    DateTimeOffset StartedAtUtc,
    DateTimeOffset CompletedAtUtc,
    IReadOnlyList<string> Failures,
    IReadOnlyList<LifecycleSimulationIterationReport> Iterations,
    IReadOnlyDictionary<string, int> AggregateBucketCounts,
    LifecycleSimulationDirectoryTotals FinalDirectoryTotals,
    IReadOnlyList<LifecycleSimulationDirectoryUserState> FinalDirectoryUsers);

internal sealed record LifecycleSimulationIterationReport(
    int Order,
    string Name,
    string RunId,
    string RunStatus,
    IReadOnlyDictionary<string, int> BucketCounts,
    IReadOnlyList<LifecycleSimulationWorkerOperationResult> WorkerOperations,
    IReadOnlyList<string> Failures);

internal sealed record LifecycleSimulationWorkerOperationResult(
    string WorkerId,
    IReadOnlyList<string> Operations);

internal sealed record LifecycleSimulationDirectoryUserState(
    string WorkerId,
    string SamAccountName,
    string DistinguishedName,
    string ParentOu,
    bool Enabled,
    string? DisplayName,
    IReadOnlyDictionary<string, string?> Attributes);

internal sealed record LifecycleSimulationDirectoryTotals(
    int TotalUsers,
    int EnabledUsers,
    int DisabledUsers);

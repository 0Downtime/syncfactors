using SyncFactors.Contracts;
using System.Text.Json;

namespace SyncFactors.Domain;

public interface IRunLifecycleService
{
    Task ExecutePlannedRunAsync(RunPlan plan, CancellationToken cancellationToken);
    Task StartRunAsync(string runId, string mode, bool dryRun, string runTrigger, string? requestedBy, int totalWorkers, string? initialAction, CancellationToken cancellationToken);
    Task RecordProgressAsync(string runId, string mode, bool dryRun, int processedWorkers, int totalWorkers, string? currentWorkerId, string? lastAction, RunTally tally, CancellationToken cancellationToken);
    Task AppendRunEntryAsync(string runId, RunEntryRecord entry, CancellationToken cancellationToken);
    Task CompleteRunAsync(string runId, string mode, bool dryRun, int totalWorkers, RunTally tally, JsonElement report, DateTimeOffset startedAt, CancellationToken cancellationToken);
    Task CancelRunAsync(string runId, string mode, bool dryRun, int processedWorkers, int totalWorkers, string? currentWorkerId, string? reason, RunTally tally, JsonElement report, DateTimeOffset startedAt, CancellationToken cancellationToken);
    Task FailRunAsync(string runId, string mode, bool dryRun, int processedWorkers, int totalWorkers, string? currentWorkerId, string errorMessage, RunTally tally, JsonElement report, DateTimeOffset startedAt, CancellationToken cancellationToken);
}

public interface IScaffoldRunPlanner
{
    RunPlan CreateBootstrapPlan(DateTimeOffset startedAt);
}

public sealed class RunLifecycleService(
    IRuntimeStatusStore runtimeStatusStore,
    IRunRepository runRepository) : IRunLifecycleService
{
    public async Task ExecutePlannedRunAsync(RunPlan plan, CancellationToken cancellationToken)
    {
        var startedAt = DateTimeOffset.UtcNow;

        await runtimeStatusStore.SaveAsync(
            new RuntimeStatus(
                Status: "InProgress",
                Stage: "Planning",
                RunId: plan.RunId,
                Mode: plan.Mode,
                DryRun: plan.DryRun,
                ProcessedWorkers: 0,
                TotalWorkers: plan.TotalWorkers,
                CurrentWorkerId: plan.InitialWorkerId,
                LastAction: plan.InitialAction,
                StartedAt: startedAt,
                LastUpdatedAt: startedAt,
                CompletedAt: null,
                ErrorMessage: null),
            cancellationToken);

        await runRepository.SaveRunAsync(
            ToRunRecord(plan, "InProgress", startedAt, completedAt: null, durationSeconds: null),
            cancellationToken);

        await runRepository.ReplaceRunEntriesAsync(plan.RunId, plan.Entries, cancellationToken);

        var completedAt = DateTimeOffset.UtcNow;

        await runRepository.SaveRunAsync(
            ToRunRecord(
                plan,
                "Succeeded",
                startedAt,
                completedAt,
                Math.Max(0, (int)(completedAt - startedAt).TotalSeconds)),
            cancellationToken);

        await runtimeStatusStore.SaveAsync(
            new RuntimeStatus(
                Status: "Idle",
                Stage: "Completed",
                RunId: plan.RunId,
                Mode: plan.Mode,
                DryRun: plan.DryRun,
                ProcessedWorkers: plan.TotalWorkers,
                TotalWorkers: plan.TotalWorkers,
                CurrentWorkerId: null,
                LastAction: $"{plan.Mode} run persisted to SQLite",
                StartedAt: startedAt,
                LastUpdatedAt: completedAt,
                CompletedAt: completedAt,
                ErrorMessage: null),
            cancellationToken);
    }

    public async Task StartRunAsync(string runId, string mode, bool dryRun, string runTrigger, string? requestedBy, int totalWorkers, string? initialAction, CancellationToken cancellationToken)
    {
        var startedAt = DateTimeOffset.UtcNow;
        await runRepository.SaveRunAsync(
            new RunRecord(
                RunId: runId,
                Path: null,
                ArtifactType: "BulkRun",
                ConfigPath: null,
                MappingConfigPath: null,
                Mode: mode,
                DryRun: dryRun,
                Status: "InProgress",
                StartedAt: startedAt,
                CompletedAt: null,
                DurationSeconds: null,
                Creates: 0,
                Updates: 0,
                Enables: 0,
                Disables: 0,
                GraveyardMoves: 0,
                Deletions: 0,
                Quarantined: 0,
                Conflicts: 0,
                GuardrailFailures: 0,
                ManualReview: 0,
                Unchanged: 0,
                Report: ParseJson(
                    $$"""
                    {
                      "kind": "bulkRun",
                      "runId": "{{runId}}",
                      "mode": "{{mode}}",
                      "runTrigger": "{{runTrigger}}",
                      "requestedBy": {{ToJsonString(requestedBy)}},
                      "dryRun": {{(dryRun ? "true" : "false")}},
                      "startedAt": "{{startedAt:O}}"
                    }
                    """),
                RunTrigger: runTrigger,
                RequestedBy: requestedBy),
            cancellationToken);

        await runtimeStatusStore.SaveAsync(
            new RuntimeStatus(
                Status: "InProgress",
                Stage: "Starting",
                RunId: runId,
                Mode: mode,
                DryRun: dryRun,
                ProcessedWorkers: 0,
                TotalWorkers: totalWorkers,
                CurrentWorkerId: null,
                LastAction: initialAction,
                StartedAt: startedAt,
                LastUpdatedAt: startedAt,
                CompletedAt: null,
                ErrorMessage: null),
            cancellationToken);
    }

    public Task AppendRunEntryAsync(string runId, RunEntryRecord entry, CancellationToken cancellationToken)
    {
        _ = runId;
        return runRepository.AppendRunEntryAsync(entry, cancellationToken);
    }

    public async Task RecordProgressAsync(string runId, string mode, bool dryRun, int processedWorkers, int totalWorkers, string? currentWorkerId, string? lastAction, RunTally tally, CancellationToken cancellationToken)
    {
        var existing = await runRepository.GetRunAsync(runId, cancellationToken);
        var startedAt = existing?.Run.StartedAt ?? DateTimeOffset.UtcNow;
        await runRepository.SaveRunAsync(
            new RunRecord(
                RunId: runId,
                Path: null,
                ArtifactType: existing?.Run.ArtifactType ?? "BulkRun",
                ConfigPath: existing?.Run.ConfigPath,
                MappingConfigPath: existing?.Run.MappingConfigPath,
                Mode: mode,
                DryRun: dryRun,
                Status: "InProgress",
                StartedAt: startedAt,
                CompletedAt: null,
                DurationSeconds: null,
                Creates: tally.Creates,
                Updates: tally.Updates,
                Enables: tally.Enables,
                Disables: tally.Disables,
                GraveyardMoves: tally.GraveyardMoves,
                Deletions: tally.Deletions,
                Quarantined: tally.Quarantined,
                Conflicts: tally.Conflicts,
                GuardrailFailures: tally.GuardrailFailures,
                ManualReview: tally.ManualReview,
                Unchanged: tally.Unchanged,
                Report: existing?.Report ?? ParseJson("""{"kind":"bulkRun"}"""),
                RunTrigger: existing?.Run.RunTrigger ?? "AdHoc",
                RequestedBy: existing?.Run.RequestedBy),
            cancellationToken);

        await runtimeStatusStore.SaveAsync(
            new RuntimeStatus(
                Status: "InProgress",
                Stage: mode,
                RunId: runId,
                Mode: mode,
                DryRun: dryRun,
                ProcessedWorkers: processedWorkers,
                TotalWorkers: totalWorkers,
                CurrentWorkerId: currentWorkerId,
                LastAction: lastAction,
                StartedAt: startedAt,
                LastUpdatedAt: DateTimeOffset.UtcNow,
                CompletedAt: null,
                ErrorMessage: null),
            cancellationToken);
    }

    public async Task CompleteRunAsync(string runId, string mode, bool dryRun, int totalWorkers, RunTally tally, JsonElement report, DateTimeOffset startedAt, CancellationToken cancellationToken)
    {
        var completedAt = DateTimeOffset.UtcNow;
        var existing = await runRepository.GetRunAsync(runId, cancellationToken);
        await runRepository.SaveRunAsync(
            new RunRecord(
                RunId: runId,
                Path: null,
                ArtifactType: "BulkRun",
                ConfigPath: null,
                MappingConfigPath: null,
                Mode: mode,
                DryRun: dryRun,
                Status: "Succeeded",
                StartedAt: startedAt,
                CompletedAt: completedAt,
                DurationSeconds: Math.Max(0, (int)(completedAt - startedAt).TotalSeconds),
                Creates: tally.Creates,
                Updates: tally.Updates,
                Enables: tally.Enables,
                Disables: tally.Disables,
                GraveyardMoves: tally.GraveyardMoves,
                Deletions: tally.Deletions,
                Quarantined: tally.Quarantined,
                Conflicts: tally.Conflicts,
                GuardrailFailures: tally.GuardrailFailures,
                ManualReview: tally.ManualReview,
                Unchanged: tally.Unchanged,
                Report: report,
                RunTrigger: existing?.Run.RunTrigger ?? "AdHoc",
                RequestedBy: existing?.Run.RequestedBy),
            cancellationToken);

        await runtimeStatusStore.SaveAsync(
            new RuntimeStatus(
                Status: "Idle",
                Stage: "Completed",
                RunId: runId,
                Mode: mode,
                DryRun: dryRun,
                ProcessedWorkers: totalWorkers,
                TotalWorkers: totalWorkers,
                CurrentWorkerId: null,
                LastAction: $"{mode} run completed.",
                StartedAt: startedAt,
                LastUpdatedAt: completedAt,
                CompletedAt: completedAt,
                ErrorMessage: null),
            cancellationToken);
    }

    public async Task FailRunAsync(string runId, string mode, bool dryRun, int processedWorkers, int totalWorkers, string? currentWorkerId, string errorMessage, RunTally tally, JsonElement report, DateTimeOffset startedAt, CancellationToken cancellationToken)
    {
        var completedAt = DateTimeOffset.UtcNow;
        var existing = await runRepository.GetRunAsync(runId, cancellationToken);
        await runRepository.SaveRunAsync(
            new RunRecord(
                RunId: runId,
                Path: null,
                ArtifactType: "BulkRun",
                ConfigPath: null,
                MappingConfigPath: null,
                Mode: mode,
                DryRun: dryRun,
                Status: "Failed",
                StartedAt: startedAt,
                CompletedAt: completedAt,
                DurationSeconds: Math.Max(0, (int)(completedAt - startedAt).TotalSeconds),
                Creates: tally.Creates,
                Updates: tally.Updates,
                Enables: tally.Enables,
                Disables: tally.Disables,
                GraveyardMoves: tally.GraveyardMoves,
                Deletions: tally.Deletions,
                Quarantined: tally.Quarantined,
                Conflicts: tally.Conflicts,
                GuardrailFailures: tally.GuardrailFailures,
                ManualReview: tally.ManualReview,
                Unchanged: tally.Unchanged,
                Report: report,
                RunTrigger: existing?.Run.RunTrigger ?? "AdHoc",
                RequestedBy: existing?.Run.RequestedBy),
            cancellationToken);

        await runtimeStatusStore.SaveAsync(
            new RuntimeStatus(
                Status: "Failed",
                Stage: mode,
                RunId: runId,
                Mode: mode,
                DryRun: dryRun,
                ProcessedWorkers: processedWorkers,
                TotalWorkers: totalWorkers,
                CurrentWorkerId: currentWorkerId,
                LastAction: errorMessage,
                StartedAt: startedAt,
                LastUpdatedAt: completedAt,
                CompletedAt: completedAt,
                ErrorMessage: errorMessage),
            cancellationToken);
    }

    public async Task CancelRunAsync(string runId, string mode, bool dryRun, int processedWorkers, int totalWorkers, string? currentWorkerId, string? reason, RunTally tally, JsonElement report, DateTimeOffset startedAt, CancellationToken cancellationToken)
    {
        var completedAt = DateTimeOffset.UtcNow;
        var existing = await runRepository.GetRunAsync(runId, cancellationToken);
        var message = string.IsNullOrWhiteSpace(reason) ? "Run canceled." : reason;
        await runRepository.SaveRunAsync(
            new RunRecord(
                RunId: runId,
                Path: null,
                ArtifactType: "BulkRun",
                ConfigPath: null,
                MappingConfigPath: null,
                Mode: mode,
                DryRun: dryRun,
                Status: "Canceled",
                StartedAt: startedAt,
                CompletedAt: completedAt,
                DurationSeconds: Math.Max(0, (int)(completedAt - startedAt).TotalSeconds),
                Creates: tally.Creates,
                Updates: tally.Updates,
                Enables: tally.Enables,
                Disables: tally.Disables,
                GraveyardMoves: tally.GraveyardMoves,
                Deletions: tally.Deletions,
                Quarantined: tally.Quarantined,
                Conflicts: tally.Conflicts,
                GuardrailFailures: tally.GuardrailFailures,
                ManualReview: tally.ManualReview,
                Unchanged: tally.Unchanged,
                Report: report,
                RunTrigger: existing?.Run.RunTrigger ?? "AdHoc",
                RequestedBy: existing?.Run.RequestedBy),
            cancellationToken);

        await runtimeStatusStore.SaveAsync(
            new RuntimeStatus(
                Status: "Idle",
                Stage: "Canceled",
                RunId: runId,
                Mode: mode,
                DryRun: dryRun,
                ProcessedWorkers: 0,
                TotalWorkers: 0,
                CurrentWorkerId: null,
                LastAction: message,
                StartedAt: startedAt,
                LastUpdatedAt: completedAt,
                CompletedAt: completedAt,
                ErrorMessage: null),
            cancellationToken);
    }

    private static RunRecord ToRunRecord(
        RunPlan plan,
        string status,
        DateTimeOffset startedAt,
        DateTimeOffset? completedAt,
        int? durationSeconds)
    {
        return new RunRecord(
            RunId: plan.RunId,
            Path: null,
            ArtifactType: plan.ArtifactType,
            ConfigPath: null,
            MappingConfigPath: null,
            Mode: plan.Mode,
            DryRun: plan.DryRun,
            Status: status,
            StartedAt: startedAt,
            CompletedAt: completedAt,
            DurationSeconds: durationSeconds,
            Creates: plan.Tally.Creates,
            Updates: plan.Tally.Updates,
            Enables: plan.Tally.Enables,
            Disables: plan.Tally.Disables,
            GraveyardMoves: plan.Tally.GraveyardMoves,
            Deletions: plan.Tally.Deletions,
            Quarantined: plan.Tally.Quarantined,
            Conflicts: plan.Tally.Conflicts,
            GuardrailFailures: plan.Tally.GuardrailFailures,
            ManualReview: plan.Tally.ManualReview,
            Unchanged: plan.Tally.Unchanged,
            Report: plan.Report,
            RunTrigger: "AdHoc",
            RequestedBy: null);
    }

    private static JsonElement ParseJson(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    private static string ToJsonString(string? value)
    {
        return value is null
            ? "null"
            : JsonSerializer.Serialize(value);
    }
}

public sealed class ScaffoldRunPlanner : IScaffoldRunPlanner
{
    public RunPlan CreateBootstrapPlan(DateTimeOffset startedAt)
    {
        var runId = $"bootstrap-{startedAt:yyyyMMddHHmmss}";

        return new RunPlan(
            RunId: runId,
            ArtifactType: "Bootstrap",
            Mode: "Bootstrap",
            DryRun: true,
            TotalWorkers: 1,
            InitialWorkerId: "bootstrap-worker",
            InitialAction: "Writing native .NET scaffold state",
            Report: ParseJson(
                """
                {
                  "kind": "bootstrap",
                  "operations": [
                    {
                      "workerId": "bootstrap-worker",
                      "bucket": "creates",
                      "operationType": "CreateUser",
                      "target": {
                        "samAccountName": "bootstrap.worker"
                      },
                      "after": {
                        "targetOu": "OU=Bootstrap,DC=example,DC=com"
                      }
                    }
                  ]
                }
                """),
            Entries:
            [
                new RunEntryRecord(
                    EntryId: $"{runId}:creates:bootstrap-worker:0",
                    RunId: runId,
                    Bucket: "creates",
                    BucketIndex: 0,
                    WorkerId: "bootstrap-worker",
                    SamAccountName: "bootstrap.worker",
                    Reason: null,
                    ReviewCategory: null,
                    ReviewCaseType: null,
                    StartedAt: startedAt,
                    Item: ParseJson(
                        """
                        {
                          "workerId": "bootstrap-worker",
                          "samAccountName": "bootstrap.worker",
                          "targetOu": "OU=Bootstrap,DC=example,DC=com",
                          "changedAttributeDetails": [
                            {
                              "targetAttribute": "displayName",
                              "sourceField": "preferredName",
                              "currentAdValue": null,
                              "proposedValue": "Bootstrap Worker"
                            }
                          ]
                        }
                        """))
            ],
            Tally: new RunTally(
                Creates: 1,
                Updates: 0,
                Enables: 0,
                Disables: 0,
                GraveyardMoves: 0,
                Deletions: 0,
                Quarantined: 0,
                Conflicts: 0,
                GuardrailFailures: 0,
                ManualReview: 0,
                Unchanged: 0));
    }

    private static JsonElement ParseJson(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }
}

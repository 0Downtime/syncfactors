using SyncFactors.Contracts;
using System.Text.Json;

namespace SyncFactors.Domain;

public interface IRunLifecycleService
{
    Task ExecutePlannedRunAsync(RunPlan plan, CancellationToken cancellationToken);
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
            Report: plan.Report);
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

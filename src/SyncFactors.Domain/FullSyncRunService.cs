using Microsoft.Extensions.Logging;
using SyncFactors.Contracts;
using System.Text.Json;

namespace SyncFactors.Domain;

public sealed class FullSyncRunService(
    IWorkerSource workerSource,
    IWorkerPlanningService planningService,
    IDirectoryMutationCommandBuilder mutationCommandBuilder,
    IDirectoryCommandGateway directoryCommandGateway,
    IRunRepository runRepository,
    IRuntimeStatusStore runtimeStatusStore,
    WorkerRunSettings settings,
    ILogger<FullSyncRunService> logger) : IFullSyncRunService
{
    public async Task<RunLaunchResult> LaunchAsync(LaunchFullRunRequest request, CancellationToken cancellationToken)
    {
        if (!request.DryRun && !request.AcknowledgeRealSync)
        {
            throw new InvalidOperationException("Acknowledge the real AD sync before starting a live run.");
        }

        var startedAt = DateTimeOffset.UtcNow;
        var runId = $"full-sync-{startedAt:yyyyMMddHHmmss}";
        var mode = request.DryRun ? "FullSyncDryRun" : "FullSyncLive";
        var startStatus = new RuntimeStatus(
            Status: "InProgress",
            Stage: "Planning",
            RunId: runId,
            Mode: mode,
            DryRun: request.DryRun,
            ProcessedWorkers: 0,
            TotalWorkers: 0,
            CurrentWorkerId: null,
            LastAction: "Enumerating workers for full sync",
            StartedAt: startedAt,
            LastUpdatedAt: startedAt,
            CompletedAt: null,
            ErrorMessage: null);

        if (!await runtimeStatusStore.TryStartAsync(startStatus, cancellationToken))
        {
            throw new InvalidOperationException("Another sync run is already in progress.");
        }

        var workers = new List<WorkerSnapshot>();
        var entries = new List<RunEntryRecord>();
        var operations = new List<object>();
        var tally = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var runStatus = "Succeeded";
        string? errorMessage = null;
        var runRecordSaved = false;

        try
        {
            await runRepository.SaveRunAsync(
                new RunRecord(
                    RunId: runId,
                    Path: null,
                    ArtifactType: "FullSync",
                    ConfigPath: null,
                    MappingConfigPath: null,
                    Mode: mode,
                    DryRun: request.DryRun,
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
                    Report: ToJsonElement(new { kind = "fullSyncRun", dryRun = request.DryRun, operations = Array.Empty<object>() }),
                    RunTrigger: "AdHoc",
                    RequestedBy: "Dashboard"),
                cancellationToken);
            runRecordSaved = true;

            await foreach (var worker in workerSource.ListWorkersAsync(cancellationToken))
            {
                workers.Add(worker);
            }

            logger.LogInformation(
                "Starting full sync run. RunId={RunId} DryRun={DryRun} WorkerCount={WorkerCount}",
                runId,
                request.DryRun,
                workers.Count);

            await runtimeStatusStore.SaveAsync(
                startStatus with
                {
                    TotalWorkers = workers.Count,
                    CurrentWorkerId = workers.FirstOrDefault()?.WorkerId,
                    LastAction = "Building full sync plan"
                },
                cancellationToken);

            entries.Capacity = workers.Count;
            operations.Capacity = workers.Count;
            var createCount = 0;

            for (var index = 0; index < workers.Count; index++)
            {
                var outcome = await BuildWorkerOutcomeAsync(
                    runId,
                    workers[index],
                    request.DryRun,
                    index,
                    createCount,
                    cancellationToken);
                entries.Add(outcome.Entry);
                operations.Add(outcome.Operation);
                IncrementBucket(tally, outcome.Bucket);

                if (string.Equals(outcome.Bucket, "creates", StringComparison.OrdinalIgnoreCase))
                {
                    createCount++;
                }

                if (!outcome.Succeeded)
                {
                    runStatus = "Failed";
                    errorMessage ??= outcome.Message;
                }

                await runtimeStatusStore.SaveAsync(
                    new RuntimeStatus(
                        Status: "InProgress",
                        Stage: request.DryRun ? "DryRun" : "Applying",
                        RunId: runId,
                        Mode: mode,
                        DryRun: request.DryRun,
                        ProcessedWorkers: index + 1,
                        TotalWorkers: workers.Count,
                        CurrentWorkerId: index + 1 < workers.Count ? workers[index + 1].WorkerId : null,
                        LastAction: outcome.Message,
                        StartedAt: startedAt,
                        LastUpdatedAt: DateTimeOffset.UtcNow,
                        CompletedAt: null,
                        ErrorMessage: null),
                    cancellationToken);
            }

            var completedAt = DateTimeOffset.UtcNow;
            await runRepository.SaveRunAsync(BuildRunRecord(
                runId,
                mode,
                request.DryRun,
                runStatus,
                startedAt,
                completedAt,
                tally,
                operations,
                requestedBy: "Dashboard"), cancellationToken);

            await runRepository.ReplaceRunEntriesAsync(runId, entries, cancellationToken);

            await runtimeStatusStore.SaveAsync(
                new RuntimeStatus(
                    Status: runStatus == "Succeeded" ? "Idle" : "Failed",
                    Stage: "Completed",
                    RunId: runId,
                    Mode: mode,
                    DryRun: request.DryRun,
                    ProcessedWorkers: workers.Count,
                    TotalWorkers: workers.Count,
                    CurrentWorkerId: null,
                    LastAction: runStatus == "Succeeded" ? $"{mode} completed for {workers.Count} workers." : errorMessage,
                    StartedAt: startedAt,
                    LastUpdatedAt: completedAt,
                    CompletedAt: completedAt,
                    ErrorMessage: runStatus == "Succeeded" ? null : errorMessage),
                cancellationToken);

            return new RunLaunchResult(
                RunId: runId,
                Status: runStatus,
                DryRun: request.DryRun,
                Message: runStatus == "Succeeded"
                    ? $"{mode} completed for {workers.Count} workers."
                    : $"Full sync completed with failures. {errorMessage}");
        }
        catch (Exception ex)
        {
            var completedAt = DateTimeOffset.UtcNow;
            logger.LogError(ex, "Full sync run failed unexpectedly. RunId={RunId}", runId);

            if (runRecordSaved)
            {
                await runRepository.SaveRunAsync(BuildRunRecord(
                    runId,
                    mode,
                    request.DryRun,
                    "Failed",
                    startedAt,
                    completedAt,
                    tally,
                    operations,
                    requestedBy: "Dashboard",
                    errorMessage: ex.Message), cancellationToken);
            }

            await runtimeStatusStore.SaveAsync(
                new RuntimeStatus(
                    Status: "Failed",
                    Stage: "Completed",
                    RunId: runId,
                    Mode: mode,
                    DryRun: request.DryRun,
                    ProcessedWorkers: entries.Count,
                    TotalWorkers: workers.Count,
                    CurrentWorkerId: null,
                    LastAction: ex.Message,
                    StartedAt: startedAt,
                    LastUpdatedAt: completedAt,
                    CompletedAt: completedAt,
                    ErrorMessage: ex.Message),
                cancellationToken);
            throw;
        }
    }

    private async Task<WorkerOutcome> BuildWorkerOutcomeAsync(
        string runId,
        WorkerSnapshot worker,
        bool dryRun,
        int index,
        int createCount,
        CancellationToken cancellationToken)
    {
        try
        {
            var plan = await planningService.PlanAsync(worker, logPath: null, cancellationToken);
            var bucket = ResolveExecutionBucket(plan);
            var action = plan.Identity.MatchedExistingUser ? "UpdateUser" : "CreateUser";
            var succeeded = true;
            var message = dryRun
                ? $"Planned {action} for {plan.Identity.SamAccountName}."
                : $"Prepared {action} for {plan.Identity.SamAccountName}.";
            string? distinguishedName = plan.DirectoryUser.DistinguishedName;

            if (string.Equals(bucket, "creates", StringComparison.OrdinalIgnoreCase) &&
                createCount + 1 > settings.MaxCreatesPerRun)
            {
                bucket = "guardrailFailures";
                message = $"Create guardrail exceeded. MaxCreatesPerRun={settings.MaxCreatesPerRun}.";
            }

            if (!dryRun && (bucket == "creates" || bucket == "updates"))
            {
                try
                {
                    var result = await directoryCommandGateway.ExecuteAsync(mutationCommandBuilder.Build(plan), cancellationToken);
                    succeeded = result.Succeeded;
                    distinguishedName = result.DistinguishedName ?? distinguishedName;
                    message = result.Message;
                    if (!result.Succeeded)
                    {
                        bucket = "conflicts";
                    }
                }
                catch (Exception ex)
                {
                    succeeded = false;
                    bucket = "conflicts";
                    message = ex.Message;
                }
            }
            else if (bucket == "manualReview")
            {
                message = plan.Reason ?? $"Worker {worker.WorkerId} requires manual review before sync.";
            }
            else if (bucket == "unchanged")
            {
                message = $"No synced attributes changed for {worker.WorkerId}.";
            }

            var item = ToJsonElement(new
            {
                workerId = plan.Worker.WorkerId,
                samAccountName = plan.Identity.SamAccountName,
                targetOu = plan.Worker.TargetOu,
                managerDistinguishedName = plan.ManagerDistinguishedName,
                reason = message,
                matchedExistingUser = plan.Identity.MatchedExistingUser,
                proposedEnable = plan.DirectoryUser.Enabled ?? true,
                currentEnabled = plan.DirectoryUser.Enabled,
                changedAttributeDetails = plan.AttributeChanges
                    .Where(change => change.Changed)
                    .Select(change => new
                    {
                        targetAttribute = change.Attribute,
                        sourceField = change.Source,
                        currentAdValue = change.Before == "(unset)" ? null : change.Before,
                        proposedValue = change.After == "(unset)" ? null : change.After
                    })
                    .ToArray(),
                succeeded,
                message
            });

            return new WorkerOutcome(
                Bucket: bucket,
                Entry: new RunEntryRecord(
                    EntryId: $"{runId}:{bucket}:{worker.WorkerId}:{index}",
                    RunId: runId,
                    Bucket: bucket,
                    BucketIndex: index,
                    WorkerId: worker.WorkerId,
                    SamAccountName: plan.Identity.SamAccountName,
                    Reason: message,
                    ReviewCategory: bucket == "manualReview" ? plan.ReviewCategory : null,
                    ReviewCaseType: bucket == "manualReview" ? plan.ReviewCaseType : null,
                    StartedAt: DateTimeOffset.UtcNow,
                    Item: item),
                Operation: new
                {
                    workerId = worker.WorkerId,
                    bucket,
                    operationType = action,
                    target = new { samAccountName = plan.Identity.SamAccountName },
                    before = new
                    {
                        distinguishedName = plan.DirectoryUser.DistinguishedName,
                        parentOu = ExtractParentOu(plan.DirectoryUser.DistinguishedName)
                    },
                    after = new
                    {
                        distinguishedName,
                        targetOu = plan.Worker.TargetOu
                    },
                    message,
                    succeeded
                },
                Succeeded: succeeded || bucket is "manualReview" or "unchanged" or "guardrailFailures",
                Message: message);
        }
        catch (Exception ex)
        {
            return new WorkerOutcome(
                Bucket: "conflicts",
                Entry: new RunEntryRecord(
                    EntryId: $"{runId}:conflicts:{worker.WorkerId}:{index}",
                    RunId: runId,
                    Bucket: "conflicts",
                    BucketIndex: index,
                    WorkerId: worker.WorkerId,
                    SamAccountName: null,
                    Reason: ex.Message,
                    ReviewCategory: "ExternalSystem",
                    ReviewCaseType: "WorkerPlanningFailed",
                    StartedAt: DateTimeOffset.UtcNow,
                    Item: ToJsonElement(new
                    {
                        workerId = worker.WorkerId,
                        bucket = "conflicts",
                        reason = ex.Message,
                        succeeded = false
                    })),
                Operation: new
                {
                    workerId = worker.WorkerId,
                    bucket = "conflicts",
                    operationType = "PlanningFailed",
                    target = new { samAccountName = (string?)null },
                    before = new { distinguishedName = (string?)null, parentOu = (string?)null },
                    after = new { distinguishedName = (string?)null, targetOu = worker.TargetOu },
                    message = ex.Message,
                    succeeded = false
                },
                Succeeded: false,
                Message: ex.Message);
        }
    }

    private static RunRecord BuildRunRecord(
        string runId,
        string mode,
        bool dryRun,
        string status,
        DateTimeOffset startedAt,
        DateTimeOffset completedAt,
        IDictionary<string, int> tally,
        IReadOnlyList<object> operations,
        string requestedBy,
        string? errorMessage = null)
    {
        return new RunRecord(
            RunId: runId,
            Path: null,
            ArtifactType: "FullSync",
            ConfigPath: null,
            MappingConfigPath: null,
            Mode: mode,
            DryRun: dryRun,
            Status: status,
            StartedAt: startedAt,
            CompletedAt: completedAt,
            DurationSeconds: Math.Max(0, (int)(completedAt - startedAt).TotalSeconds),
            Creates: GetBucketCount(tally, "creates"),
            Updates: GetBucketCount(tally, "updates"),
            Enables: GetBucketCount(tally, "enables"),
            Disables: GetBucketCount(tally, "disables"),
            GraveyardMoves: GetBucketCount(tally, "graveyardMoves"),
            Deletions: GetBucketCount(tally, "deletions"),
            Quarantined: GetBucketCount(tally, "quarantined"),
            Conflicts: GetBucketCount(tally, "conflicts"),
            GuardrailFailures: GetBucketCount(tally, "guardrailFailures"),
            ManualReview: GetBucketCount(tally, "manualReview"),
            Unchanged: GetBucketCount(tally, "unchanged"),
            Report: ToJsonElement(new { kind = "fullSyncRun", dryRun, operations, totals = tally, errorMessage }),
            RunTrigger: "AdHoc",
            RequestedBy: requestedBy);
    }

    private static string ResolveExecutionBucket(PlannedWorkerAction plan)
    {
        return plan.Bucket == "updates" && plan.AttributeChanges.All(change => !change.Changed)
            ? "unchanged"
            : plan.Bucket;
    }

    private static string? ExtractParentOu(string? distinguishedName)
    {
        if (string.IsNullOrWhiteSpace(distinguishedName))
        {
            return null;
        }

        var commaIndex = distinguishedName.IndexOf(',');
        return commaIndex < 0 || commaIndex + 1 >= distinguishedName.Length
            ? null
            : distinguishedName[(commaIndex + 1)..];
    }

    private static void IncrementBucket(IDictionary<string, int> tally, string bucket)
    {
        tally[bucket] = GetBucketCount(tally, bucket) + 1;
    }

    private static int GetBucketCount(IDictionary<string, int> tally, string bucket)
    {
        return tally.TryGetValue(bucket, out var count) ? count : 0;
    }

    private static JsonElement ToJsonElement<T>(T value)
    {
        return JsonSerializer.SerializeToElement(value);
    }

    private sealed record WorkerOutcome(
        string Bucket,
        RunEntryRecord Entry,
        object Operation,
        bool Succeeded,
        string Message);
}

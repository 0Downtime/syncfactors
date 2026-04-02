using System.Text.Json;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using SyncFactors.Contracts;

namespace SyncFactors.Domain;

public sealed class BulkRunCoordinator(
    IWorkerSource workerSource,
    IDeltaSyncService deltaSyncService,
    IRunQueueStore runQueueStore,
    IWorkerPlanningService planningService,
    IDirectoryMutationCommandBuilder mutationCommandBuilder,
    IDirectoryCommandGateway directoryCommandGateway,
    IRunLifecycleService runLifecycleService,
    WorkerRunSettings settings,
    ILogger<BulkRunCoordinator> logger,
    TimeProvider timeProvider)
{
    public async Task<string> ExecuteAsync(RunQueueRequest request, int maxDegreeOfParallelism, CancellationToken cancellationToken)
    {
        using var runCancellationSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var runCancellationToken = runCancellationSource.Token;
        var cancellationMonitor = MonitorCancellationAsync(request.RequestId, runCancellationSource, cancellationToken);
        var syncScope = await DetermineSyncScopeAsync(cancellationToken);
        var workers = new List<WorkerSnapshot>();
        GuardrailExceededException? guardrailFailure = null;
        try
        {
            await foreach (var worker in workerSource.ListWorkersAsync(WorkerListingMode.DeltaPreferred, runCancellationToken))
            {
                workers.Add(worker);
            }
        }
        catch (OperationCanceledException)
        {
            if (!cancellationToken.IsCancellationRequested &&
                await runQueueStore.IsCancellationRequestedAsync(request.RequestId, CancellationToken.None))
            {
                throw new RunCanceledException(runId: null, "Run canceled by operator.");
            }

            throw;
        }

        var runId = $"bulk-{timeProvider.GetUtcNow():yyyyMMddHHmmssfff}";
        var startedAt = timeProvider.GetUtcNow();
        var totalWorkers = workers.Count;
        logger.LogInformation(
            "Starting bulk run. RunId={RunId} Trigger={RunTrigger} DryRun={DryRun} RequestedBy={RequestedBy} Workers={Workers}",
            runId,
            request.RunTrigger,
            request.DryRun,
            request.RequestedBy,
            totalWorkers);
        var channel = Channel.CreateUnbounded<WorkerRunResult>();
        var tally = new RunTally(0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0);
        var processedWorkers = 0;
        var createCount = 0;
        var disableCount = 0;

        await runLifecycleService.StartRunAsync(
            runId,
            mode: "BulkSync",
            dryRun: request.DryRun,
            runTrigger: request.RunTrigger,
            requestedBy: request.RequestedBy,
            totalWorkers: totalWorkers,
            initialAction: $"Executing queued request {request.RequestId}",
            cancellationToken);

        var writerTask = Task.Run(async () =>
        {
            await foreach (var result in channel.Reader.ReadAllAsync(cancellationToken))
            {
                processedWorkers++;
                tally = AddToTally(tally, result.Bucket);
                var entry = new RunEntryRecord(
                    EntryId: $"{runId}:{result.Bucket}:{result.WorkerId}:{processedWorkers - 1}",
                    RunId: runId,
                    Bucket: result.Bucket,
                    BucketIndex: processedWorkers - 1,
                    WorkerId: result.WorkerId,
                    SamAccountName: result.SamAccountName,
                    Reason: result.Reason,
                    ReviewCategory: result.ReviewCategory,
                    ReviewCaseType: result.ReviewCaseType,
                    StartedAt: startedAt,
                    Item: result.Item);

                await runLifecycleService.AppendRunEntryAsync(runId, entry, cancellationToken);
                await runLifecycleService.RecordProgressAsync(
                    runId,
                    mode: "BulkSync",
                    dryRun: request.DryRun,
                    processedWorkers: processedWorkers,
                    totalWorkers: totalWorkers,
                    currentWorkerId: result.WorkerId,
                    lastAction: result.Reason ?? result.Action ?? result.Bucket,
                    tally: tally,
                    cancellationToken);
            }
        }, cancellationToken);

        try
        {
            await Parallel.ForEachAsync(
                workers,
                new ParallelOptions
                {
                    CancellationToken = runCancellationToken,
                    MaxDegreeOfParallelism = Math.Max(1, maxDegreeOfParallelism)
                },
                async (worker, ct) =>
                {
                    ct.ThrowIfCancellationRequested();
                    try
                    {
                        var plan = await planningService.PlanAsync(worker, logPath: null, ct);
                        var bucket = ResolveExecutionBucket(plan);
                        string? reason = plan.Reason;
                        string? action = null;
                        var applied = false;
                        var succeeded = true;

                        if (plan.CanAutoApply && string.Equals(bucket, "creates", StringComparison.OrdinalIgnoreCase))
                        {
                            var nextCreateCount = Interlocked.Increment(ref createCount);
                            if (nextCreateCount > settings.MaxCreatesPerRun)
                            {
                                bucket = "guardrailFailures";
                                reason = $"Create guardrail exceeded. MaxCreatesPerRun={settings.MaxCreatesPerRun}.";
                                var guardrailItem = BuildEntryItem(plan, request.DryRun, bucket, action: null, applied: false, succeeded: false, reason);
                                await channel.Writer.WriteAsync(
                                    new WorkerRunResult(
                                        WorkerId: worker.WorkerId,
                                        Bucket: bucket,
                                        SamAccountName: plan.Identity.SamAccountName,
                                        Reason: reason,
                                        ReviewCategory: plan.ReviewCategory,
                                        ReviewCaseType: plan.ReviewCaseType,
                                        Action: null,
                                        Applied: false,
                                        Succeeded: false,
                                        OperationSummary: BuildOperationSummary(plan, action: null, bucket),
                                        DiffRows: plan.AttributeChanges.Select(change => new DiffRow(change.Attribute, change.Source, change.Before, change.After, change.Changed)).ToArray(),
                                        Item: guardrailItem),
                                    ct);
                                Interlocked.CompareExchange(ref guardrailFailure, new GuardrailExceededException(runId, reason), null);
                                runCancellationSource.Cancel();
                                return;
                            }
                        }

                        if (plan.CanAutoApply &&
                            (string.Equals(bucket, "disables", StringComparison.OrdinalIgnoreCase) ||
                             string.Equals(bucket, "graveyardMoves", StringComparison.OrdinalIgnoreCase)) &&
                            plan.Operations.Any(operation => string.Equals(operation.Kind, "DisableUser", StringComparison.OrdinalIgnoreCase)))
                        {
                            var nextDisableCount = Interlocked.Increment(ref disableCount);
                            if (nextDisableCount > settings.MaxDisablesPerRun)
                            {
                                bucket = "guardrailFailures";
                                reason = $"Disable guardrail exceeded. MaxDisablesPerRun={settings.MaxDisablesPerRun}.";
                                var guardrailItem = BuildEntryItem(plan, request.DryRun, bucket, action: null, applied: false, succeeded: false, reason);
                                await channel.Writer.WriteAsync(
                                    new WorkerRunResult(
                                        WorkerId: worker.WorkerId,
                                        Bucket: bucket,
                                        SamAccountName: plan.Identity.SamAccountName,
                                        Reason: reason,
                                        ReviewCategory: plan.ReviewCategory,
                                        ReviewCaseType: plan.ReviewCaseType,
                                        Action: null,
                                        Applied: false,
                                        Succeeded: false,
                                        OperationSummary: BuildOperationSummary(plan, action: null, bucket),
                                        DiffRows: plan.AttributeChanges.Select(change => new DiffRow(change.Attribute, change.Source, change.Before, change.After, change.Changed)).ToArray(),
                                        Item: guardrailItem),
                                    ct);
                                Interlocked.CompareExchange(ref guardrailFailure, new GuardrailExceededException(runId, reason), null);
                                runCancellationSource.Cancel();
                                return;
                            }
                        }

                        if (plan.CanAutoApply)
                        {
                            action = plan.PrimaryAction;
                        }

                        if (!request.DryRun && action is not null && (string.Equals(bucket, "creates", StringComparison.OrdinalIgnoreCase) || string.Equals(bucket, "updates", StringComparison.OrdinalIgnoreCase)))
                        {
                            try
                            {
                                var command = mutationCommandBuilder.Build(plan);
                                var result = await directoryCommandGateway.ExecuteAsync(command, ct);
                                applied = true;
                                succeeded = result.Succeeded;
                                reason = result.Message;
                                if (!result.Succeeded)
                                {
                                    bucket = "conflicts";
                                }
                            }
                            catch (Exception ex)
                            {
                                applied = true;
                                succeeded = false;
                                bucket = "conflicts";
                                reason = ex.Message;
                            }
                        }

                        var item = BuildEntryItem(plan, request.DryRun, bucket, action, applied, succeeded, reason);
                        await channel.Writer.WriteAsync(
                            new WorkerRunResult(
                                WorkerId: worker.WorkerId,
                                Bucket: bucket,
                                SamAccountName: plan.Identity.SamAccountName,
                                Reason: reason,
                                ReviewCategory: plan.ReviewCategory,
                                ReviewCaseType: plan.ReviewCaseType,
                                Action: action,
                                Applied: applied,
                                Succeeded: succeeded,
                                OperationSummary: BuildOperationSummary(plan, action, bucket),
                                DiffRows: plan.AttributeChanges.Select(change => new DiffRow(change.Attribute, change.Source, change.Before, change.After, change.Changed)).ToArray(),
                                Item: item),
                            ct);
                    }
                    catch (Exception ex)
                    {
                        logger.LogError(ex, "Worker planning failed. WorkerId={WorkerId}", worker.WorkerId);
                        await channel.Writer.WriteAsync(
                            new WorkerRunResult(
                                WorkerId: worker.WorkerId,
                                Bucket: "conflicts",
                                SamAccountName: null,
                                Reason: ex.Message,
                                ReviewCategory: "ExternalSystem",
                                ReviewCaseType: "WorkerPlanningFailed",
                                Action: null,
                                Applied: false,
                                Succeeded: false,
                                OperationSummary: null,
                                DiffRows: [],
                                Item: BuildPlanningFailureItem(worker, ex.Message)),
                            ct);
                    }
                });

            channel.Writer.Complete();
            await writerTask;
            await runLifecycleService.CompleteRunAsync(
                runId,
                mode: "BulkSync",
                dryRun: request.DryRun,
                totalWorkers: totalWorkers,
                tally: tally,
                report: BuildReport(runId, request, tally, totalWorkers, startedAt, syncScope),
                startedAt: startedAt,
                cancellationToken);

            if (!request.DryRun && ShouldAdvanceDeltaCheckpoint(tally))
            {
                await deltaSyncService.RecordSuccessfulRunAsync(cancellationToken);
            }

            return runId;
        }
        catch (OperationCanceledException)
        {
            if (guardrailFailure is not null)
            {
                channel.Writer.TryComplete(guardrailFailure);
                try
                {
                    await writerTask;
                }
                catch
                {
                }

                await runLifecycleService.FailRunAsync(
                    runId,
                    mode: "BulkSync",
                    dryRun: request.DryRun,
                    processedWorkers: processedWorkers,
                    totalWorkers: totalWorkers,
                    currentWorkerId: null,
                    errorMessage: guardrailFailure.Message,
                    tally: tally,
                    report: BuildReport(runId, request, tally, totalWorkers, startedAt, syncScope),
                    startedAt: startedAt,
                    cancellationToken);
                throw guardrailFailure;
            }

            if (!cancellationToken.IsCancellationRequested &&
                await runQueueStore.IsCancellationRequestedAsync(request.RequestId, CancellationToken.None))
            {
                channel.Writer.TryComplete();
                try
                {
                    await writerTask;
                }
                catch
                {
                }

                await runLifecycleService.CancelRunAsync(
                    runId,
                    mode: "BulkSync",
                    dryRun: request.DryRun,
                    processedWorkers: processedWorkers,
                    totalWorkers: totalWorkers,
                    currentWorkerId: null,
                    reason: "Run canceled by operator.",
                    tally: tally,
                    report: BuildReport(runId, request, tally, totalWorkers, startedAt, syncScope),
                    startedAt: startedAt,
                    cancellationToken);
                throw new RunCanceledException(runId, "Run canceled by operator.");
            }

            throw;
        }
        catch (Exception ex)
        {
            channel.Writer.TryComplete(ex);
            try
            {
                await writerTask;
            }
            catch
            {
            }

            await runLifecycleService.FailRunAsync(
                runId,
                mode: "BulkSync",
                dryRun: request.DryRun,
                processedWorkers: processedWorkers,
                totalWorkers: totalWorkers,
                currentWorkerId: null,
                errorMessage: ex.Message,
                tally: tally,
                report: BuildReport(runId, request, tally, totalWorkers, startedAt, syncScope),
                startedAt: startedAt,
                cancellationToken);
            throw;
        }
        finally
        {
            runCancellationSource.Cancel();
            try
            {
                await cancellationMonitor;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
            }
        }
    }

    private async Task MonitorCancellationAsync(string requestId, CancellationTokenSource runCancellationSource, CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
        while (!runCancellationSource.IsCancellationRequested &&
               await timer.WaitForNextTickAsync(cancellationToken))
        {
            if (!await runQueueStore.IsCancellationRequestedAsync(requestId, cancellationToken))
            {
                continue;
            }

            logger.LogInformation("Cancellation requested for run queue item {RequestId}.", requestId);
            runCancellationSource.Cancel();
            return;
        }
    }

    private static RunTally AddToTally(RunTally tally, string bucket)
    {
        return bucket switch
        {
            "creates" => tally with { Creates = tally.Creates + 1 },
            "updates" => tally with { Updates = tally.Updates + 1 },
            "enables" => tally with { Enables = tally.Enables + 1 },
            "disables" => tally with { Disables = tally.Disables + 1 },
            "graveyardMoves" => tally with { GraveyardMoves = tally.GraveyardMoves + 1 },
            "deletions" => tally with { Deletions = tally.Deletions + 1 },
            "manualReview" => tally with { ManualReview = tally.ManualReview + 1 },
            "guardrailFailures" => tally with { GuardrailFailures = tally.GuardrailFailures + 1 },
            "conflicts" => tally with { Conflicts = tally.Conflicts + 1 },
            "quarantined" => tally with { Quarantined = tally.Quarantined + 1 },
            _ => tally with { Unchanged = tally.Unchanged + 1 }
        };
    }

    private static OperationSummary BuildOperationSummary(PlannedWorkerAction plan, string? action, string bucket)
    {
        var summaryAction = action ?? bucket;
        return new OperationSummary(
            Action: summaryAction,
            Effect: plan.AttributeChanges.Count(change => change.Changed) == 0
                ? "No attribute changes."
                : $"{plan.AttributeChanges.Count(change => change.Changed)} attribute changes.",
            TargetOu: plan.TargetOu,
            FromOu: plan.CurrentOu,
            ToOu: plan.TargetOu);
    }

    private static bool ShouldAdvanceDeltaCheckpoint(RunTally tally)
    {
        return tally.Conflicts == 0 &&
               tally.GuardrailFailures == 0 &&
               tally.ManualReview == 0;
    }

    private async Task<string> DetermineSyncScopeAsync(CancellationToken cancellationToken)
    {
        var deltaWindow = await deltaSyncService.GetWindowAsync(cancellationToken);
        if (!deltaWindow.Enabled || !deltaWindow.HasCheckpoint || string.IsNullOrWhiteSpace(deltaWindow.Filter))
        {
            return "Bulk full scan";
        }

        return "Delta";
    }

    private static string ResolveExecutionBucket(PlannedWorkerAction plan)
    {
        return string.Equals(plan.Bucket, "updates", StringComparison.OrdinalIgnoreCase) &&
               plan.AttributeChanges.All(change => !change.Changed)
            ? "unchanged"
            : plan.Bucket;
    }

    private static JsonElement BuildEntryItem(PlannedWorkerAction plan, bool dryRun, string bucket, string? action, bool applied, bool succeeded, string? reason)
    {
        var changedRows = plan.AttributeChanges
            .Where(change => change.Changed)
            .Select(change =>
                $$"""
                {
                  "targetAttribute": "{{Escape(change.Attribute)}}",
                  "sourceField": {{ToJsonString(change.Source)}},
                  "currentAdValue": {{ToJsonString(change.Before == "(unset)" ? null : change.Before)}},
                  "proposedValue": {{ToJsonString(change.After == "(unset)" ? null : change.After)}}
                }
                """);

        return ParseJson(
            $$"""
            {
              "workerId": "{{Escape(plan.Worker.WorkerId)}}",
              "samAccountName": "{{Escape(plan.Identity.SamAccountName)}}",
              "targetOu": "{{Escape(plan.Worker.TargetOu)}}",
              "currentOu": {{ToJsonString(plan.CurrentOu)}},
              "managerDistinguishedName": {{ToJsonString(plan.ManagerDistinguishedName)}},
              "reviewCategory": {{ToJsonString(plan.ReviewCategory)}},
              "reviewCaseType": {{ToJsonString(plan.ReviewCaseType)}},
              "reason": {{ToJsonString(reason)}},
              "bucket": "{{Escape(bucket)}}",
              "action": {{ToJsonString(action)}},
              "dryRun": {{(dryRun ? "true" : "false")}},
              "applied": {{(applied ? "true" : "false")}},
              "succeeded": {{(succeeded ? "true" : "false")}},
              "currentEnabled": {{ToJsonNullableBoolean(plan.CurrentEnabled)}},
              "proposedEnable": {{ToJsonNullableBoolean(plan.TargetEnabled)}},
              "operations": [
                {{string.Join(",", plan.Operations.Select(operation =>
                    $$"""
                    {
                      "kind": "{{Escape(operation.Kind)}}",
                      "targetOu": {{ToJsonString(operation.TargetOu)}}
                    }
                    """))}}
              ],
              "managerRequired": {{(!string.IsNullOrWhiteSpace(plan.Worker.Attributes.TryGetValue("managerId", out var managerId) ? managerId : null) ? "true" : "false")}},
              "changedAttributeDetails": [{{string.Join(",", changedRows)}}]
            }
            """);
    }

    private static JsonElement BuildReport(string runId, RunQueueRequest request, RunTally tally, int totalWorkers, DateTimeOffset startedAt, string syncScope)
    {
        return ParseJson(
            $$"""
            {
              "kind": "bulkRun",
              "syncScope": "{{Escape(syncScope)}}",
              "runId": "{{runId}}",
              "requestId": "{{request.RequestId}}",
              "mode": "{{request.Mode}}",
              "runTrigger": "{{request.RunTrigger}}",
              "requestedBy": {{ToJsonString(request.RequestedBy)}},
              "dryRun": {{(request.DryRun ? "true" : "false")}},
              "startedAt": "{{startedAt:O}}",
              "totalWorkers": {{totalWorkers}},
              "tally": {
                "creates": {{tally.Creates}},
                "updates": {{tally.Updates}},
                "enables": {{tally.Enables}},
                "disables": {{tally.Disables}},
                "graveyardMoves": {{tally.GraveyardMoves}},
                "deletions": {{tally.Deletions}},
                "manualReview": {{tally.ManualReview}},
                "guardrailFailures": {{tally.GuardrailFailures}},
                "conflicts": {{tally.Conflicts}},
                "quarantined": {{tally.Quarantined}},
                "unchanged": {{tally.Unchanged}}
              },
              "operations": []
            }
            """);
    }

    private static JsonElement ParseJson(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    private static string Escape(string value)
    {
        var serialized = JsonSerializer.Serialize(value);
        return serialized[1..^1];
    }

    private static string ToJsonString(string? value) => value is null ? "null" : $"\"{Escape(value)}\"";

    private static string ToJsonNullableBoolean(bool? value) => value.HasValue ? (value.Value ? "true" : "false") : "null";

    private static JsonElement BuildPlanningFailureItem(WorkerSnapshot worker, string? reason)
    {
        return ParseJson(
            $$"""
            {
              "workerId": "{{Escape(worker.WorkerId)}}",
              "samAccountName": null,
              "targetOu": {{ToJsonString(worker.TargetOu)}},
              "managerDistinguishedName": null,
              "reviewCategory": "ExternalSystem",
              "reviewCaseType": "WorkerPlanningFailed",
              "reason": {{ToJsonString(reason)}},
              "bucket": "conflicts",
              "action": null,
              "dryRun": true,
              "applied": false,
              "succeeded": false,
              "managerRequired": {{(!string.IsNullOrWhiteSpace(worker.Attributes.TryGetValue("managerId", out var managerId) ? managerId : null) ? "true" : "false")}},
              "changedAttributeDetails": []
            }
            """);
    }
}

public sealed class RunCanceledException(string? runId, string message) : Exception(message)
{
    public string? RunId { get; } = runId;
}

public sealed class GuardrailExceededException(string runId, string message) : Exception(message)
{
    public string RunId { get; } = runId;
}

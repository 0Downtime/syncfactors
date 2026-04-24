using System.Text.Json;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using SyncFactors.Contracts;

namespace SyncFactors.Domain;

public sealed class BulkRunCoordinator(
    IWorkerSource workerSource,
    IDeltaSyncService deltaSyncService,
    IRunQueueStore runQueueStore,
    IGraveyardRetentionStore graveyardRetentionStore,
    IWorkerPlanningService planningService,
    IDirectoryMutationCommandBuilder mutationCommandBuilder,
    IDirectoryCommandGateway directoryCommandGateway,
    IDirectoryGateway directoryGateway,
    IRunLifecycleService runLifecycleService,
    RealSyncSettings realSyncSettings,
    WorkerRunSettings settings,
    LifecyclePolicySettings lifecycleSettings,
    ILogger<BulkRunCoordinator> logger,
    TimeProvider timeProvider,
    IRunCaptureMetadataProvider? runCaptureMetadataProvider = null)
{
    public async Task<string> ExecuteAsync(RunQueueRequest request, int maxDegreeOfParallelism, CancellationToken cancellationToken)
    {
        if (!request.DryRun && !realSyncSettings.Enabled)
        {
            throw new InvalidOperationException("Real AD sync is disabled for this environment.");
        }

        using var runCancellationSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var runCancellationToken = runCancellationSource.Token;
        var cancellationMonitor = MonitorCancellationAsync(request.RequestId, runCancellationSource, cancellationToken);
        var syncScope = await DetermineSyncScopeAsync(cancellationToken);
        var extractionStartedAt = timeProvider.GetUtcNow();
        var runId = $"bulk-{timeProvider.GetUtcNow():yyyyMMddHHmmssfff}";
        var startedAt = timeProvider.GetUtcNow();
        var captureMetadataProvider = runCaptureMetadataProvider ?? NullRunCaptureMetadataProvider.Instance;
        var captureMetadata = captureMetadataProvider.Create(runId, request.DryRun, syncScope);
        using var logScope = RunLoggingScope.Begin(logger, runId, mode: "BulkSync", request.RequestId);
        var tally = new RunTally(0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0);
        RunPopulationTotals? populationTotals = null;

        await runLifecycleService.StartRunAsync(
            runId,
            mode: "BulkSync",
            dryRun: request.DryRun,
            runTrigger: request.RunTrigger,
            requestedBy: request.RequestedBy,
            totalWorkers: 0,
            initialAction: $"Starting queued request {request.RequestId}",
            cancellationToken);

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
                await runLifecycleService.CancelRunAsync(
                    runId,
                    mode: "BulkSync",
                    dryRun: request.DryRun,
                    processedWorkers: 0,
                    totalWorkers: 0,
                    currentWorkerId: null,
                    reason: "Run canceled by operator.",
                    tally: tally,
                    report: BuildReport(runId, request, tally, totalWorkers: 0, startedAt, syncScope, populationTotals),
                    startedAt: startedAt,
                    cancellationToken);
                throw new RunCanceledException(runId, "Run canceled by operator.");
            }

            throw;
        }
        catch (Exception ex)
        {
            await runLifecycleService.FailRunAsync(
                runId,
                mode: "BulkSync",
                dryRun: request.DryRun,
                processedWorkers: 0,
                totalWorkers: 0,
                currentWorkerId: null,
                errorMessage: ex.Message,
                tally: tally,
                report: BuildReport(runId, request, tally, totalWorkers: 0, startedAt, syncScope, populationTotals),
                startedAt: startedAt,
                cancellationToken);
            throw;
        }

        var totalWorkers = workers.Count;
        var successFactorsActiveCount = RunPopulationTotalsBuilder.CountSuccessFactorsActiveWorkers(workers, lifecycleSettings);
        logger.LogInformation(
            "Starting bulk run. RunId={RunId} Trigger={RunTrigger} DryRun={DryRun} RequestedBy={RequestedBy} Workers={Workers}",
            runId,
            request.RunTrigger,
            request.DryRun,
            request.RequestedBy,
            totalWorkers);
        var channel = Channel.CreateUnbounded<WorkerRunResult>();
        var processedWorkers = 0;
        var createCount = 0;
        var disableCount = 0;

        async Task<RunPopulationTotals?> GetPopulationTotalsAsync()
        {
            if (populationTotals is not null)
            {
                return populationTotals;
            }

            try
            {
                populationTotals = await RunPopulationTotalsBuilder.BuildAsync(
                    workers,
                    directoryGateway,
                    lifecycleSettings,
                    cancellationToken);
            }
            catch (Exception ex)
            {
                logger.LogWarning(
                    ex,
                    "Population totals could not be captured for run {RunId}. SuccessFactorsActive={SuccessFactorsActive} ActiveOu={ActiveOu}",
                    runId,
                    successFactorsActiveCount,
                    lifecycleSettings.ActiveOu);
            }

            return populationTotals;
        }

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
                        DirectoryCommandResult? commandResult = null;

                        if (plan.CanAutoApply && string.Equals(bucket, "creates", StringComparison.OrdinalIgnoreCase))
                        {
                            var nextCreateCount = Interlocked.Increment(ref createCount);
                            if (nextCreateCount > settings.MaxCreatesPerRun)
                            {
                                bucket = "guardrailFailures";
                                reason = $"Create guardrail exceeded. MaxCreatesPerRun={settings.MaxCreatesPerRun}.";
                                var guardrailItem = BuildEntryItem(runId, request.DryRun, syncScope, plan, bucket, action: null, applied: false, succeeded: false, reason, plannedCommand: null, commandResult: null, captureMetadata);
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
                                var guardrailItem = BuildEntryItem(runId, request.DryRun, syncScope, plan, bucket, action: null, applied: false, succeeded: false, reason, plannedCommand: null, commandResult: null, captureMetadata);
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

                        var plannedCommand = action is null || string.Equals(bucket, "guardrailFailures", StringComparison.OrdinalIgnoreCase)
                            ? null
                            : mutationCommandBuilder.Build(plan);

                        if (!request.DryRun && action is not null)
                        {
                            try
                            {
                                var result = await directoryCommandGateway.ExecuteAsync(plannedCommand!, ct);
                                commandResult = result;
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

                        var item = BuildEntryItem(runId, request.DryRun, syncScope, plan, bucket, action, applied, succeeded, reason, plannedCommand, commandResult, captureMetadata);
                        await UpdateGraveyardRetentionAsync(plan, ct);
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
                                Item: BuildPlanningFailureItem(runId, request.DryRun, syncScope, worker, ex.Message, captureMetadata)),
                            ct);
                    }
                });

            channel.Writer.Complete();
            await writerTask;
            populationTotals = await GetPopulationTotalsAsync();
            await runLifecycleService.CompleteRunAsync(
                runId,
                mode: "BulkSync",
                dryRun: request.DryRun,
                totalWorkers: totalWorkers,
                tally: tally,
                report: BuildReport(runId, request, tally, totalWorkers, startedAt, syncScope, populationTotals),
                startedAt: startedAt,
                cancellationToken);

            if (!request.DryRun && ShouldAdvanceDeltaCheckpoint(tally))
            {
                await deltaSyncService.RecordSuccessfulRunAsync(extractionStartedAt, cancellationToken);
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
                    report: BuildReport(runId, request, tally, totalWorkers, startedAt, syncScope, await GetPopulationTotalsAsync()),
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
                    report: BuildReport(runId, request, tally, totalWorkers, startedAt, syncScope, await GetPopulationTotalsAsync()),
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
                report: BuildReport(runId, request, tally, totalWorkers, startedAt, syncScope, await GetPopulationTotalsAsync()),
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

    private JsonElement BuildEntryItem(
        string runId,
        bool dryRun,
        string syncScope,
        PlannedWorkerAction plan,
        string bucket,
        string? action,
        bool applied,
        bool succeeded,
        string? reason,
        DirectoryMutationCommand? plannedCommand,
        DirectoryCommandResult? commandResult,
        RunCaptureMetadata captureMetadata) =>
        RunEntrySnapshotBuilder.Build(
            runId,
            dryRun,
            syncScope,
            plan,
            bucket,
            action,
            applied,
            succeeded,
            reason,
            plannedCommand,
            commandResult,
            captureMetadata,
            lifecycleSettings.DirectoryIdentityAttribute);

    private static JsonElement BuildReport(
        string runId,
        RunQueueRequest request,
        RunTally tally,
        int totalWorkers,
        DateTimeOffset startedAt,
        string syncScope,
        RunPopulationTotals? populationTotals)
    {
        return JsonSerializer.SerializeToElement(new
        {
            kind = "bulkRun",
            syncScope,
            runId,
            requestId = request.RequestId,
            mode = request.Mode,
            runTrigger = request.RunTrigger,
            requestedBy = request.RequestedBy,
            dryRun = request.DryRun,
            startedAt,
            totalWorkers,
            tally = new
            {
                creates = tally.Creates,
                updates = tally.Updates,
                enables = tally.Enables,
                disables = tally.Disables,
                graveyardMoves = tally.GraveyardMoves,
                deletions = tally.Deletions,
                manualReview = tally.ManualReview,
                guardrailFailures = tally.GuardrailFailures,
                conflicts = tally.Conflicts,
                quarantined = tally.Quarantined,
                unchanged = tally.Unchanged
            },
            populationTotals = populationTotals is null
                ? null
                : new
                {
                    successFactorsActive = populationTotals.SuccessFactorsActive,
                    activeDirectoryEnabled = populationTotals.ActiveDirectoryEnabled,
                    difference = populationTotals.Difference,
                    activeOu = populationTotals.ActiveOu
                },
            operations = Array.Empty<object>()
        });
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

    private static JsonElement BuildPlanningFailureItem(
        string runId,
        bool dryRun,
        string syncScope,
        WorkerSnapshot worker,
        string? reason,
        RunCaptureMetadata captureMetadata) =>
        RunEntrySnapshotBuilder.BuildPlanningFailure(runId, dryRun, syncScope, worker, reason, captureMetadata);

    private async Task UpdateGraveyardRetentionAsync(PlannedWorkerAction plan, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(plan.Worker.WorkerId))
        {
            return;
        }

        if (string.Equals(plan.TargetOu, lifecycleSettings.GraveyardOu, StringComparison.OrdinalIgnoreCase) &&
            !plan.TargetEnabled &&
            !string.IsNullOrWhiteSpace(plan.DirectoryUser.SamAccountName))
        {
            await graveyardRetentionStore.UpsertObservedAsync(
                new GraveyardRetentionRecord(
                    WorkerId: plan.Worker.WorkerId,
                    SamAccountName: plan.DirectoryUser.SamAccountName ?? plan.Identity.SamAccountName,
                    DisplayName: plan.DirectoryUser.DisplayName ?? $"{plan.Worker.PreferredName} {plan.Worker.LastName}".Trim(),
                    DistinguishedName: plan.DirectoryUser.DistinguishedName,
                    Status: ResolveSourceAttribute(plan.Worker.Attributes, lifecycleSettings.InactiveStatusField) ?? string.Empty,
                    EndDateUtc: ParseSourceDate(ResolveSourceAttribute(plan.Worker.Attributes, "endDate")),
                    LastObservedAtUtc: timeProvider.GetUtcNow(),
                    Active: true),
                cancellationToken);
            return;
        }

        await graveyardRetentionStore.ResolveAsync(plan.Worker.WorkerId, cancellationToken);
    }

    private static string? ResolveSourceAttribute(IReadOnlyDictionary<string, string?> attributes, string key)
    {
        if (attributes.TryGetValue(key, out var value))
        {
            return value;
        }

        var normalized = SourceAttributePathNormalizer.Normalize(key);
        return attributes.TryGetValue(normalized, out value) ? value : null;
    }

    private static DateTimeOffset? ParseSourceDate(string? value)
    {
        return SourceDateParser.TryParse(value, out var parsed) ? parsed : null;
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

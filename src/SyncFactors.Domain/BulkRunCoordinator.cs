using System.Text.Json;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using SyncFactors.Contracts;

namespace SyncFactors.Domain;

public sealed class BulkRunCoordinator(
    IWorkerSource workerSource,
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
        var workers = new List<WorkerSnapshot>();
        await foreach (var worker in workerSource.ListWorkersAsync(cancellationToken))
        {
            workers.Add(worker);
        }

        var runId = $"bulk-{timeProvider.GetUtcNow():yyyyMMddHHmmssfff}";
        var startedAt = timeProvider.GetUtcNow();
        var totalWorkers = workers.Count;
        var channel = Channel.CreateUnbounded<WorkerRunResult>();
        var tally = new RunTally(0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0);
        var processedWorkers = 0;
        var createCount = 0;
        var operationLock = new object();

        await runLifecycleService.StartRunAsync(
            runId,
            mode: "BulkSync",
            dryRun: request.DryRun,
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
                    CancellationToken = cancellationToken,
                    MaxDegreeOfParallelism = Math.Max(1, maxDegreeOfParallelism)
                },
                async (worker, ct) =>
                {
                    var plan = await planningService.PlanAsync(worker, logPath: null, ct);
                    var bucket = plan.Bucket;
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
                        }
                    }

                    if (plan.CanAutoApply && string.Equals(bucket, "updates", StringComparison.OrdinalIgnoreCase))
                    {
                        action = "UpdateUser";
                    }
                    else if (plan.CanAutoApply && string.Equals(bucket, "creates", StringComparison.OrdinalIgnoreCase))
                    {
                        action = "CreateUser";
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
                });

            channel.Writer.Complete();
            await writerTask;
            await runLifecycleService.CompleteRunAsync(
                runId,
                mode: "BulkSync",
                dryRun: request.DryRun,
                totalWorkers: totalWorkers,
                tally: tally,
                report: BuildReport(runId, request, tally, totalWorkers, startedAt),
                startedAt: startedAt,
                cancellationToken);

            return runId;
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
                report: BuildReport(runId, request, tally, totalWorkers, startedAt),
                startedAt: startedAt,
                cancellationToken);
            throw;
        }
    }

    private static RunTally AddToTally(RunTally tally, string bucket)
    {
        return bucket switch
        {
            "creates" => tally with { Creates = tally.Creates + 1 },
            "updates" => tally with { Updates = tally.Updates + 1 },
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
            TargetOu: plan.Worker.TargetOu,
            FromOu: plan.DirectoryUser.DistinguishedName,
            ToOu: plan.Worker.TargetOu);
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
              "managerDistinguishedName": {{ToJsonString(plan.ManagerDistinguishedName)}},
              "reviewCategory": {{ToJsonString(plan.ReviewCategory)}},
              "reviewCaseType": {{ToJsonString(plan.ReviewCaseType)}},
              "reason": {{ToJsonString(reason)}},
              "bucket": "{{Escape(bucket)}}",
              "action": {{ToJsonString(action)}},
              "dryRun": {{(dryRun ? "true" : "false")}},
              "applied": {{(applied ? "true" : "false")}},
              "succeeded": {{(succeeded ? "true" : "false")}},
              "managerRequired": {{(!string.IsNullOrWhiteSpace(plan.Worker.Attributes.TryGetValue("managerId", out var managerId) ? managerId : null) ? "true" : "false")}},
              "changedAttributeDetails": [{{string.Join(",", changedRows)}}]
            }
            """);
    }

    private static JsonElement BuildReport(string runId, RunQueueRequest request, RunTally tally, int totalWorkers, DateTimeOffset startedAt)
    {
        return ParseJson(
            $$"""
            {
              "kind": "bulkRun",
              "runId": "{{runId}}",
              "requestId": "{{request.RequestId}}",
              "mode": "{{request.Mode}}",
              "dryRun": {{(request.DryRun ? "true" : "false")}},
              "startedAt": "{{startedAt:O}}",
              "totalWorkers": {{totalWorkers}},
              "tally": {
                "creates": {{tally.Creates}},
                "updates": {{tally.Updates}},
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
}

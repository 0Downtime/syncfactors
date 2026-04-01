using System.Text.Json;
using Microsoft.Extensions.Logging;
using SyncFactors.Contracts;

namespace SyncFactors.Domain;

public sealed class DeleteAllUsersCoordinator(
    IWorkerSource workerSource,
    IRunQueueStore runQueueStore,
    IDirectoryGateway directoryGateway,
    IDirectoryCommandGateway directoryCommandGateway,
    IRunLifecycleService runLifecycleService,
    WorkerRunSettings settings,
    ILogger<DeleteAllUsersCoordinator> logger,
    TimeProvider timeProvider)
{
    public async Task<string> ExecuteAsync(RunQueueRequest request, CancellationToken cancellationToken)
    {
        using var runCancellationSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var runCancellationToken = runCancellationSource.Token;
        var cancellationMonitor = MonitorCancellationAsync(request.RequestId, runCancellationSource, cancellationToken);

        var workers = new List<WorkerSnapshot>();
        try
        {
            await foreach (var worker in workerSource.ListWorkersAsync(WorkerListingMode.Full, runCancellationToken))
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

        var runId = $"delete-all-{timeProvider.GetUtcNow():yyyyMMddHHmmssfff}";
        var startedAt = timeProvider.GetUtcNow();
        var totalWorkers = workers.Count;
        var tally = new RunTally(0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0);
        var processedWorkers = 0;
        var deletionCount = 0;

        logger.LogWarning(
            "Starting delete-all-users run. RunId={RunId} Trigger={RunTrigger} RequestedBy={RequestedBy} Workers={Workers}",
            runId,
            request.RunTrigger,
            request.RequestedBy,
            totalWorkers);

        await runLifecycleService.StartRunAsync(
            runId,
            mode: "DeleteAllUsers",
            dryRun: request.DryRun,
            runTrigger: request.RunTrigger,
            requestedBy: request.RequestedBy,
            totalWorkers: totalWorkers,
            initialAction: $"Executing queued delete request {request.RequestId}",
            cancellationToken);

        try
        {
            foreach (var worker in workers)
            {
                runCancellationToken.ThrowIfCancellationRequested();

                var result = await DeleteWorkerAsync(worker, request.DryRun, deletionCount, runCancellationToken);
                processedWorkers++;
                tally = AddToTally(tally, result.Bucket);

                if (string.Equals(result.Bucket, "deletions", StringComparison.OrdinalIgnoreCase))
                {
                    deletionCount++;
                }

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
                    mode: "DeleteAllUsers",
                    dryRun: request.DryRun,
                    processedWorkers: processedWorkers,
                    totalWorkers: totalWorkers,
                    currentWorkerId: worker.WorkerId,
                    lastAction: result.Reason ?? result.Action ?? result.Bucket,
                    tally: tally,
                    cancellationToken);

                if (string.Equals(result.Bucket, "guardrailFailures", StringComparison.OrdinalIgnoreCase))
                {
                    throw new GuardrailExceededException(runId, result.Reason ?? "Deletion guardrail exceeded.");
                }
            }

            await runLifecycleService.CompleteRunAsync(
                runId,
                mode: "DeleteAllUsers",
                dryRun: request.DryRun,
                totalWorkers: totalWorkers,
                tally: tally,
                report: BuildReport(runId, request, tally, totalWorkers, startedAt),
                startedAt: startedAt,
                cancellationToken);

            return runId;
        }
        catch (OperationCanceledException)
        {
            if (!cancellationToken.IsCancellationRequested &&
                await runQueueStore.IsCancellationRequestedAsync(request.RequestId, CancellationToken.None))
            {
                await runLifecycleService.CancelRunAsync(
                    runId,
                    mode: "DeleteAllUsers",
                    dryRun: request.DryRun,
                    processedWorkers: processedWorkers,
                    totalWorkers: totalWorkers,
                    currentWorkerId: null,
                    reason: "Run canceled by operator.",
                    tally: tally,
                    report: BuildReport(runId, request, tally, totalWorkers, startedAt),
                    startedAt: startedAt,
                    cancellationToken);
                throw new RunCanceledException(runId, "Run canceled by operator.");
            }

            throw;
        }
        catch (GuardrailExceededException ex)
        {
            await runLifecycleService.FailRunAsync(
                runId,
                mode: "DeleteAllUsers",
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
        catch (Exception ex)
        {
            await runLifecycleService.FailRunAsync(
                runId,
                mode: "DeleteAllUsers",
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

    private async Task<WorkerRunResult> DeleteWorkerAsync(
        WorkerSnapshot worker,
        bool dryRun,
        int deletionCount,
        CancellationToken cancellationToken)
    {
        try
        {
            var directoryUser = await directoryGateway.FindByWorkerAsync(worker, cancellationToken);
            if (directoryUser is null || string.IsNullOrWhiteSpace(directoryUser.DistinguishedName))
            {
                var noUserReason = "No AD user matched worker.";
                return new WorkerRunResult(
                    WorkerId: worker.WorkerId,
                    Bucket: "unchanged",
                    SamAccountName: null,
                    Reason: noUserReason,
                    ReviewCategory: null,
                    ReviewCaseType: null,
                    Action: null,
                    Applied: false,
                    Succeeded: true,
                    OperationSummary: new OperationSummary("NoOp", "No AD user found for deletion.", null, null, null),
                    DiffRows: [],
                    Item: BuildEntryItem(worker, directoryUser, dryRun, "unchanged", action: null, applied: false, succeeded: true, noUserReason));
            }

            if (deletionCount + 1 > settings.MaxDeletionsPerRun)
            {
                var reason = $"Deletion guardrail exceeded. MaxDeletionsPerRun={settings.MaxDeletionsPerRun}.";
                return new WorkerRunResult(
                    WorkerId: worker.WorkerId,
                    Bucket: "guardrailFailures",
                    SamAccountName: directoryUser.SamAccountName,
                    Reason: reason,
                    ReviewCategory: null,
                    ReviewCaseType: null,
                    Action: null,
                    Applied: false,
                    Succeeded: false,
                    OperationSummary: new OperationSummary("DeleteUser", "Deletion guardrail blocked execution.", null, DirectoryDistinguishedName.GetParentOu(directoryUser.DistinguishedName), null),
                    DiffRows: [],
                    Item: BuildEntryItem(worker, directoryUser, dryRun, "guardrailFailures", action: null, applied: false, succeeded: false, reason));
            }

            var action = "DeleteUser";
            var applied = false;
            var succeeded = true;
            var reasonMessage = dryRun
                ? $"Dry-run planned deletion for AD user {directoryUser.SamAccountName ?? worker.WorkerId}."
                : $"Deleted AD user {directoryUser.SamAccountName ?? worker.WorkerId}.";
            var bucket = "deletions";

            if (!dryRun)
            {
                try
                {
                    var result = await directoryCommandGateway.ExecuteAsync(
                        BuildDeleteCommand(worker, directoryUser),
                        cancellationToken);
                    applied = true;
                    succeeded = result.Succeeded;
                    reasonMessage = result.Message;
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
                    reasonMessage = ex.Message;
                }
            }
            return new WorkerRunResult(
                WorkerId: worker.WorkerId,
                Bucket: bucket,
                SamAccountName: directoryUser.SamAccountName,
                Reason: reasonMessage,
                ReviewCategory: null,
                ReviewCaseType: null,
                Action: action,
                Applied: applied,
                Succeeded: succeeded,
                OperationSummary: new OperationSummary(action, "The AD user object will be removed.", null, DirectoryDistinguishedName.GetParentOu(directoryUser.DistinguishedName), null),
                DiffRows: [],
                Item: BuildEntryItem(worker, directoryUser, dryRun, bucket, action, applied, succeeded, reasonMessage));
        }
        catch (GuardrailExceededException)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Delete-all planning failed. WorkerId={WorkerId}", worker.WorkerId);
            return new WorkerRunResult(
                WorkerId: worker.WorkerId,
                Bucket: "conflicts",
                SamAccountName: null,
                Reason: ex.Message,
                ReviewCategory: "ExternalSystem",
                ReviewCaseType: "DeleteAllUsersFailed",
                Action: null,
                Applied: false,
                Succeeded: false,
                OperationSummary: null,
                DiffRows: [],
                Item: BuildEntryItem(worker, null, dryRun, "conflicts", action: null, applied: false, succeeded: false, ex.Message));
        }
    }

    private static DirectoryMutationCommand BuildDeleteCommand(WorkerSnapshot worker, DirectoryUserSnapshot directoryUser)
    {
        return new DirectoryMutationCommand(
            Action: "DeleteUser",
            WorkerId: worker.WorkerId,
            ManagerId: null,
            ManagerDistinguishedName: null,
            SamAccountName: directoryUser.SamAccountName ?? worker.WorkerId,
            UserPrincipalName: directoryUser.Attributes.TryGetValue("UserPrincipalName", out var upn) ? upn ?? string.Empty : string.Empty,
            Mail: directoryUser.Attributes.TryGetValue("mail", out var mail) ? mail ?? string.Empty : string.Empty,
            TargetOu: DirectoryDistinguishedName.GetParentOu(directoryUser.DistinguishedName),
            DisplayName: directoryUser.DisplayName ?? directoryUser.SamAccountName ?? worker.WorkerId,
            CurrentDistinguishedName: directoryUser.DistinguishedName,
            EnableAccount: false,
            Operations: [new DirectoryOperation("DeleteUser")],
            Attributes: new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase));
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

            logger.LogInformation("Cancellation requested for delete-all queue item {RequestId}.", requestId);
            runCancellationSource.Cancel();
            return;
        }
    }

    private static RunTally AddToTally(RunTally tally, string bucket)
    {
        return bucket switch
        {
            "deletions" => tally with { Deletions = tally.Deletions + 1 },
            "guardrailFailures" => tally with { GuardrailFailures = tally.GuardrailFailures + 1 },
            "conflicts" => tally with { Conflicts = tally.Conflicts + 1 },
            _ => tally with { Unchanged = tally.Unchanged + 1 }
        };
    }

    private static JsonElement BuildEntryItem(
        WorkerSnapshot worker,
        DirectoryUserSnapshot? directoryUser,
        bool dryRun,
        string bucket,
        string? action,
        bool applied,
        bool succeeded,
        string? reason)
    {
        return ParseJson(
            $$"""
            {
              "workerId": "{{Escape(worker.WorkerId)}}",
              "samAccountName": {{ToJsonString(directoryUser?.SamAccountName)}},
              "targetOu": {{ToJsonString(worker.TargetOu)}},
              "currentOu": {{ToJsonString(DirectoryDistinguishedName.GetParentOu(directoryUser?.DistinguishedName))}},
              "managerDistinguishedName": null,
              "reviewCategory": null,
              "reviewCaseType": null,
              "reason": {{ToJsonString(reason)}},
              "bucket": "{{Escape(bucket)}}",
              "action": {{ToJsonString(action)}},
              "dryRun": {{(dryRun ? "true" : "false")}},
              "applied": {{(applied ? "true" : "false")}},
              "succeeded": {{(succeeded ? "true" : "false")}},
              "currentEnabled": {{ToJsonNullableBoolean(directoryUser?.Enabled)}},
              "proposedEnable": false,
              "operations": [
                {{(action is null ? string.Empty : $$"""
                {
                  "kind": "{{Escape(action)}}",
                  "targetOu": null
                }
                """)}}
              ],
              "managerRequired": false,
              "changedAttributeDetails": []
            }
            """);
    }

    private static JsonElement BuildReport(string runId, RunQueueRequest request, RunTally tally, int totalWorkers, DateTimeOffset startedAt)
    {
        return ParseJson(
            $$"""
            {
              "kind": "deleteAllUsersRun",
              "syncScope": "Delete all users",
              "runId": "{{runId}}",
              "requestId": "{{request.RequestId}}",
              "mode": "{{request.Mode}}",
              "runTrigger": "{{request.RunTrigger}}",
              "requestedBy": {{ToJsonString(request.RequestedBy)}},
              "dryRun": {{(request.DryRun ? "true" : "false")}},
              "startedAt": "{{startedAt:O}}",
              "totalWorkers": {{totalWorkers}},
              "tally": {
                "deletions": {{tally.Deletions}},
                "guardrailFailures": {{tally.GuardrailFailures}},
                "conflicts": {{tally.Conflicts}},
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
}

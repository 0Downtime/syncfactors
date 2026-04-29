using System.Text.Json;
using Microsoft.Extensions.Logging;
using SyncFactors.Contracts;

namespace SyncFactors.Domain;

public sealed class GraveyardAutoDeleteCoordinator(
    GraveyardDeletionQueueService deletionQueueService,
    IGraveyardRetentionStore retentionStore,
    IDirectoryCommandGateway directoryCommandGateway,
    IRunLifecycleService runLifecycleService,
    GraveyardDeletionQueueSettings settings,
    WorkerRunSettings workerRunSettings,
    ILogger<GraveyardAutoDeleteCoordinator> logger,
    TimeProvider timeProvider)
{
    public async Task<GraveyardDeletionApprovalResult> ApproveDeleteAsync(
        string workerId,
        string? requestedBy,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(workerId))
        {
            return new GraveyardDeletionApprovalResult(false, "Worker ID is required.", null);
        }

        var snapshot = await deletionQueueService.GetSnapshotAsync(cancellationToken);
        var item = snapshot.Pending.FirstOrDefault(candidate =>
            string.Equals(candidate.WorkerId, workerId, StringComparison.OrdinalIgnoreCase));
        if (item is null)
        {
            var heldItem = snapshot.Held.FirstOrDefault(candidate =>
                string.Equals(candidate.WorkerId, workerId, StringComparison.OrdinalIgnoreCase));
            return heldItem is null
                ? new GraveyardDeletionApprovalResult(false, $"Worker {workerId} is not in the deletion queue.", null)
                : new GraveyardDeletionApprovalResult(false, $"Worker {workerId} is on hold. Remove the hold before approving deletion.", null);
        }

        if (!item.IsEligibleForDeletion)
        {
            return new GraveyardDeletionApprovalResult(false, $"Worker {workerId} is not due for deletion yet.", null);
        }

        var actor = string.IsNullOrWhiteSpace(requestedBy) ? "Admin" : requestedBy;
        var runId = $"graveyard-delete-approval-{timeProvider.GetUtcNow():yyyyMMddHHmmssfff}";
        using var logScope = RunLoggingScope.Begin(logger, runId, mode: "GraveyardDeleteApproval");
        var startedAt = timeProvider.GetUtcNow();
        var tally = new RunTally(0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0);

        await runLifecycleService.StartRunAsync(
            runId,
            mode: "GraveyardDeleteApproval",
            dryRun: false,
            runTrigger: "AdminApproval",
            requestedBy: actor,
            totalWorkers: 1,
            initialAction: $"Approving graveyard deletion for worker {item.WorkerId}.",
            cancellationToken);

        try
        {
            var result = await DeleteUserAsync(item, deletionCount: 0, cancellationToken);
            tally = AddToTally(tally, result.Bucket);

            await runLifecycleService.AppendRunEntryAsync(
                runId,
                new RunEntryRecord(
                    EntryId: $"{runId}:{result.Bucket}:{item.WorkerId}:0",
                    RunId: runId,
                    Bucket: result.Bucket,
                    BucketIndex: 0,
                    WorkerId: item.WorkerId,
                    SamAccountName: item.SamAccountName,
                    Reason: result.Reason,
                    ReviewCategory: result.ReviewCategory,
                    ReviewCaseType: result.ReviewCaseType,
                    StartedAt: startedAt,
                    Item: result.Item),
                cancellationToken);

            await runLifecycleService.RecordProgressAsync(
                runId,
                mode: "GraveyardDeleteApproval",
                dryRun: false,
                processedWorkers: 1,
                totalWorkers: 1,
                currentWorkerId: item.WorkerId,
                lastAction: result.Reason ?? result.Action ?? result.Bucket,
                tally: tally,
                cancellationToken);

            var report = BuildReport(
                runId,
                startedAt,
                totalWorkers: 1,
                tally,
                kind: "graveyardDeleteApproval",
                syncScope: "Graveyard delete approval",
                mode: "GraveyardDeleteApproval",
                runTrigger: "AdminApproval",
                requestedBy: actor);

            if (string.Equals(result.Bucket, "guardrailFailures", StringComparison.OrdinalIgnoreCase))
            {
                await runLifecycleService.FailRunAsync(
                    runId,
                    mode: "GraveyardDeleteApproval",
                    dryRun: false,
                    processedWorkers: 1,
                    totalWorkers: 1,
                    currentWorkerId: item.WorkerId,
                    errorMessage: result.Reason ?? "Deletion guardrail exceeded.",
                    tally: tally,
                    report: report,
                    startedAt: startedAt,
                    cancellationToken);
                return new GraveyardDeletionApprovalResult(false, result.Reason ?? "Deletion guardrail exceeded.", runId);
            }

            await runLifecycleService.CompleteRunAsync(
                runId,
                mode: "GraveyardDeleteApproval",
                dryRun: false,
                totalWorkers: 1,
                tally: tally,
                report: report,
                startedAt: startedAt,
                cancellationToken);

            return string.Equals(result.Bucket, "deletions", StringComparison.OrdinalIgnoreCase)
                ? new GraveyardDeletionApprovalResult(true, result.Reason ?? $"Deleted AD user {item.SamAccountName ?? item.WorkerId}.", runId)
                : new GraveyardDeletionApprovalResult(false, result.Reason ?? $"Could not delete worker {item.WorkerId}.", runId);
        }
        catch (Exception ex)
        {
            await runLifecycleService.FailRunAsync(
                runId,
                mode: "GraveyardDeleteApproval",
                dryRun: false,
                processedWorkers: 0,
                totalWorkers: 1,
                currentWorkerId: item.WorkerId,
                errorMessage: ex.Message,
                tally: tally,
                report: BuildReport(
                    runId,
                    startedAt,
                    totalWorkers: 1,
                    tally,
                    kind: "graveyardDeleteApproval",
                    syncScope: "Graveyard delete approval",
                    mode: "GraveyardDeleteApproval",
                    runTrigger: "AdminApproval",
                    requestedBy: actor),
                startedAt: startedAt,
                cancellationToken);
            return new GraveyardDeletionApprovalResult(false, ex.Message, runId);
        }
    }

    public async Task<string?> TryExecuteAsync(CancellationToken cancellationToken)
    {
        if (!settings.AutoDeleteEnabled)
        {
            return null;
        }

        var snapshot = await deletionQueueService.GetSnapshotAsync(cancellationToken);
        var dueItems = snapshot.Pending
            .Where(item => item.IsEligibleForDeletion)
            .ToArray();
        if (dueItems.Length == 0)
        {
            return null;
        }

        var runId = $"graveyard-auto-delete-{timeProvider.GetUtcNow():yyyyMMddHHmmssfff}";
        using var logScope = RunLoggingScope.Begin(logger, runId, mode: "GraveyardAutoDelete");
        var startedAt = timeProvider.GetUtcNow();
        var tally = new RunTally(0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0);
        var processedWorkers = 0;
        var deletionCount = 0;

        logger.LogWarning(
            "Starting automatic graveyard deletion run. RunId={RunId} Users={Users}",
            runId,
            dueItems.Length);

        await runLifecycleService.StartRunAsync(
            runId,
            mode: "GraveyardAutoDelete",
            dryRun: false,
            runTrigger: "GraveyardAutoDelete",
            requestedBy: "SyncFactors.Worker",
            totalWorkers: dueItems.Length,
            initialAction: "Executing automatic graveyard deletions.",
            cancellationToken);

        try
        {
            foreach (var item in dueItems)
            {
                var result = await DeleteUserAsync(item, deletionCount, cancellationToken);
                processedWorkers++;
                tally = AddToTally(tally, result.Bucket);

                if (string.Equals(result.Bucket, "deletions", StringComparison.OrdinalIgnoreCase))
                {
                    deletionCount++;
                }

                await runLifecycleService.AppendRunEntryAsync(
                    runId,
                    new RunEntryRecord(
                        EntryId: $"{runId}:{result.Bucket}:{item.WorkerId}:{processedWorkers - 1}",
                        RunId: runId,
                        Bucket: result.Bucket,
                        BucketIndex: processedWorkers - 1,
                        WorkerId: item.WorkerId,
                        SamAccountName: item.SamAccountName,
                        Reason: result.Reason,
                        ReviewCategory: result.ReviewCategory,
                        ReviewCaseType: result.ReviewCaseType,
                        StartedAt: startedAt,
                        Item: result.Item),
                    cancellationToken);

                await runLifecycleService.RecordProgressAsync(
                    runId,
                    mode: "GraveyardAutoDelete",
                    dryRun: false,
                    processedWorkers: processedWorkers,
                    totalWorkers: dueItems.Length,
                    currentWorkerId: item.WorkerId,
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
                mode: "GraveyardAutoDelete",
                dryRun: false,
                totalWorkers: dueItems.Length,
                tally: tally,
                report: BuildReport(
                    runId,
                    startedAt,
                    dueItems.Length,
                    tally,
                    kind: "graveyardAutoDelete",
                    syncScope: "Graveyard auto delete",
                    mode: "GraveyardAutoDelete",
                    runTrigger: "GraveyardAutoDelete",
                    requestedBy: "SyncFactors.Worker"),
                startedAt: startedAt,
                cancellationToken);

            return runId;
        }
        catch (GuardrailExceededException ex)
        {
            await runLifecycleService.FailRunAsync(
                runId,
                mode: "GraveyardAutoDelete",
                dryRun: false,
                processedWorkers: processedWorkers,
                totalWorkers: dueItems.Length,
                currentWorkerId: null,
                errorMessage: ex.Message,
                tally: tally,
                report: BuildReport(
                    runId,
                    startedAt,
                    dueItems.Length,
                    tally,
                    kind: "graveyardAutoDelete",
                    syncScope: "Graveyard auto delete",
                    mode: "GraveyardAutoDelete",
                    runTrigger: "GraveyardAutoDelete",
                    requestedBy: "SyncFactors.Worker"),
                startedAt: startedAt,
                cancellationToken);
            throw;
        }
        catch (Exception ex)
        {
            await runLifecycleService.FailRunAsync(
                runId,
                mode: "GraveyardAutoDelete",
                dryRun: false,
                processedWorkers: processedWorkers,
                totalWorkers: dueItems.Length,
                currentWorkerId: null,
                errorMessage: ex.Message,
                tally: tally,
                report: BuildReport(
                    runId,
                    startedAt,
                    dueItems.Length,
                    tally,
                    kind: "graveyardAutoDelete",
                    syncScope: "Graveyard auto delete",
                    mode: "GraveyardAutoDelete",
                    runTrigger: "GraveyardAutoDelete",
                    requestedBy: "SyncFactors.Worker"),
                startedAt: startedAt,
                cancellationToken);
            throw;
        }
    }

    private async Task<WorkerRunResult> DeleteUserAsync(
        GraveyardDeletionQueueItem item,
        int deletionCount,
        CancellationToken cancellationToken)
    {
        if (deletionCount + 1 > workerRunSettings.MaxDeletionsPerRun)
        {
            var reason = $"Deletion guardrail exceeded. MaxDeletionsPerRun={workerRunSettings.MaxDeletionsPerRun}.";
            return new WorkerRunResult(
                WorkerId: item.WorkerId,
                Bucket: "guardrailFailures",
                SamAccountName: item.SamAccountName,
                Reason: reason,
                ReviewCategory: null,
                ReviewCaseType: null,
                Action: null,
                Applied: false,
                Succeeded: false,
                OperationSummary: new OperationSummary("DeleteUser", "Deletion guardrail blocked execution.", null, DirectoryDistinguishedName.GetParentOu(item.DistinguishedName), null),
                DiffRows: [],
                Item: BuildEntryItem(item, "guardrailFailures", action: null, applied: false, succeeded: false, reason));
        }

        if (string.IsNullOrWhiteSpace(item.DistinguishedName))
        {
            const string reason = "No graveyard distinguished name was available for deletion.";
            return new WorkerRunResult(
                WorkerId: item.WorkerId,
                Bucket: "conflicts",
                SamAccountName: item.SamAccountName,
                Reason: reason,
                ReviewCategory: "ExternalSystem",
                ReviewCaseType: "DeleteFailed",
                Action: null,
                Applied: false,
                Succeeded: false,
                OperationSummary: null,
                DiffRows: [],
                Item: BuildEntryItem(item, "conflicts", action: null, applied: false, succeeded: false, reason));
        }

        var action = "DeleteUser";
        var applied = false;
        var succeeded = true;
        var bucket = "deletions";
        var reasonMessage = $"Deleted AD user {item.SamAccountName ?? item.WorkerId}.";

        try
        {
            var result = await directoryCommandGateway.ExecuteAsync(BuildDeleteCommand(item), cancellationToken);
            applied = true;
            succeeded = result.Succeeded;
            reasonMessage = result.Message;
            if (result.Succeeded)
            {
                await retentionStore.ResolveAsync(item.WorkerId, cancellationToken);
            }
            else
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
            logger.LogError(ex, "Automatic graveyard deletion failed. WorkerId={WorkerId}", item.WorkerId);
        }

        return new WorkerRunResult(
            WorkerId: item.WorkerId,
            Bucket: bucket,
            SamAccountName: item.SamAccountName,
            Reason: reasonMessage,
            ReviewCategory: bucket == "conflicts" ? "ExternalSystem" : null,
            ReviewCaseType: bucket == "conflicts" ? "DeleteFailed" : null,
            Action: action,
            Applied: applied,
            Succeeded: succeeded,
            OperationSummary: new OperationSummary(action, "The AD user object will be removed.", null, DirectoryDistinguishedName.GetParentOu(item.DistinguishedName), null),
            DiffRows: [],
            Item: BuildEntryItem(item, bucket, action, applied, succeeded, reasonMessage));
    }

    private static DirectoryMutationCommand BuildDeleteCommand(GraveyardDeletionQueueItem item)
    {
        return new DirectoryMutationCommand(
            Action: "DeleteUser",
            WorkerId: item.WorkerId,
            ManagerId: null,
            ManagerDistinguishedName: null,
            SamAccountName: item.SamAccountName ?? item.WorkerId,
            CommonName: item.SamAccountName ?? item.WorkerId,
            UserPrincipalName: string.Empty,
            Mail: string.Empty,
            TargetOu: DirectoryDistinguishedName.GetParentOu(item.DistinguishedName),
            DisplayName: item.DisplayName ?? item.SamAccountName ?? item.WorkerId,
            CurrentDistinguishedName: item.DistinguishedName,
            EnableAccount: false,
            Operations: [new DirectoryOperation("DeleteUser")],
            Attributes: new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase));
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
        GraveyardDeletionQueueItem item,
        string bucket,
        string? action,
        bool applied,
        bool succeeded,
        string? reason)
    {
        return ParseJson(
            $$"""
            {
              "workerId": "{{Escape(item.WorkerId)}}",
              "samAccountName": {{ToJsonString(item.SamAccountName)}},
              "targetOu": {{ToJsonString(DirectoryDistinguishedName.GetParentOu(item.DistinguishedName))}},
              "emplStatus": {{ToJsonString(item.Status)}},
              "currentOu": {{ToJsonString(DirectoryDistinguishedName.GetParentOu(item.DistinguishedName))}},
              "managerDistinguishedName": null,
              "reviewCategory": {{ToJsonString(bucket == "conflicts" ? "ExternalSystem" : null)}},
              "reviewCaseType": {{ToJsonString(bucket == "conflicts" ? "DeleteFailed" : null)}},
              "reason": {{ToJsonString(reason)}},
              "bucket": "{{Escape(bucket)}}",
              "action": {{ToJsonString(action)}},
              "dryRun": false,
              "applied": {{(applied ? "true" : "false")}},
              "succeeded": {{(succeeded ? "true" : "false")}},
              "currentEnabled": false,
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

    private static JsonElement BuildReport(
        string runId,
        DateTimeOffset startedAt,
        int totalWorkers,
        RunTally tally,
        string kind,
        string syncScope,
        string mode,
        string runTrigger,
        string requestedBy)
    {
        return ParseJson(
            $$"""
            {
              "kind": "{{Escape(kind)}}",
              "syncScope": "{{Escape(syncScope)}}",
              "runId": "{{runId}}",
              "mode": "{{Escape(mode)}}",
              "runTrigger": "{{Escape(runTrigger)}}",
              "requestedBy": "{{Escape(requestedBy)}}",
              "dryRun": false,
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

    private static string Escape(string value) =>
        value.Replace("\\", "\\\\", StringComparison.Ordinal)
             .Replace("\"", "\\\"", StringComparison.Ordinal);

    private static string ToJsonString(string? value) =>
        value is null
            ? "null"
            : $"\"{Escape(value)}\"";

    private sealed record WorkerRunResult(
        string WorkerId,
        string Bucket,
        string? SamAccountName,
        string? Reason,
        string? ReviewCategory,
        string? ReviewCaseType,
        string? Action,
        bool Applied,
        bool Succeeded,
        OperationSummary? OperationSummary,
        IReadOnlyList<DiffRow> DiffRows,
        JsonElement Item);
}

public sealed record GraveyardDeletionApprovalResult(
    bool Succeeded,
    string Message,
    string? RunId);

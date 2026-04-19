using System.Text.Json;
using Microsoft.Extensions.Logging;
using SyncFactors.Contracts;

namespace SyncFactors.Domain;

public sealed class DeleteAllUsersCoordinator(
    IRunQueueStore runQueueStore,
    IDirectoryGateway directoryGateway,
    IDirectoryCommandGateway directoryCommandGateway,
    IRunLifecycleService runLifecycleService,
    LifecyclePolicySettings lifecycleSettings,
    RealSyncSettings realSyncSettings,
    WorkerRunSettings settings,
    ILogger<DeleteAllUsersCoordinator> logger,
    TimeProvider timeProvider)
{
    public async Task<string> ExecuteAsync(RunQueueRequest request, CancellationToken cancellationToken)
    {
        if (!request.DryRun && !realSyncSettings.Enabled)
        {
            throw new InvalidOperationException("Real AD sync is disabled for this environment.");
        }

        using var runCancellationSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var runCancellationToken = runCancellationSource.Token;
        var cancellationMonitor = MonitorCancellationAsync(request.RequestId, runCancellationSource, cancellationToken);

        var users = new List<DeleteCandidate>();
        try
        {
            users = await ListDeleteCandidatesAsync(runCancellationToken);
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
        var totalWorkers = users.Count;
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
            foreach (var user in users)
            {
                runCancellationToken.ThrowIfCancellationRequested();

                var result = await DeleteUserAsync(user, request.DryRun, deletionCount, runCancellationToken);
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
                    WorkerId: user.WorkerId,
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
                    currentWorkerId: user.WorkerId,
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
                report: BuildReport(runId, request, tally, totalWorkers, startedAt, users),
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
                    report: BuildReport(runId, request, tally, totalWorkers, startedAt, users),
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
                report: BuildReport(runId, request, tally, totalWorkers, startedAt, users),
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
                report: BuildReport(runId, request, tally, totalWorkers, startedAt, users),
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

    private async Task<List<DeleteCandidate>> ListDeleteCandidatesAsync(CancellationToken cancellationToken)
    {
        var candidates = new Dictionary<string, DeleteCandidate>(StringComparer.OrdinalIgnoreCase);
        var fallbackCandidates = new Dictionary<string, DeleteCandidate>(StringComparer.OrdinalIgnoreCase);

        foreach (var ou in GetDeleteTargetOus())
        {
            var users = await directoryGateway.ListUsersInOuAsync(ou, cancellationToken);
            foreach (var directoryUser in users)
            {
                var candidate = BuildDeleteCandidate(directoryUser, ou);
                var dedupeKey = ResolveCandidateDeduplicationKey(candidate);
                if (string.IsNullOrWhiteSpace(candidate.DistinguishedName))
                {
                    fallbackCandidates.TryAdd(dedupeKey, candidate);
                    continue;
                }

                candidates.TryAdd(dedupeKey, candidate);
            }
        }

        return [.. candidates.Values
            .Concat(fallbackCandidates.Values)
            .OrderBy(candidate => candidate.SamAccountName ?? candidate.WorkerId, StringComparer.OrdinalIgnoreCase)
            .ThenBy(candidate => candidate.DistinguishedName ?? candidate.SourceOu, StringComparer.OrdinalIgnoreCase)];
    }

    private async Task<WorkerRunResult> DeleteUserAsync(
        DeleteCandidate user,
        bool dryRun,
        int deletionCount,
        CancellationToken cancellationToken)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(user.DistinguishedName))
            {
                const string missingDnReason = "No AD distinguished name was available for deletion.";
                return new WorkerRunResult(
                    WorkerId: user.WorkerId,
                    Bucket: "conflicts",
                    SamAccountName: user.SamAccountName,
                    Reason: missingDnReason,
                    ReviewCategory: "ExternalSystem",
                    ReviewCaseType: "DeleteAllUsersFailed",
                    Action: null,
                    Applied: false,
                    Succeeded: false,
                    OperationSummary: null,
                    DiffRows: [],
                    Item: BuildEntryItem(user, dryRun, "conflicts", action: null, applied: false, succeeded: false, missingDnReason));
            }

            if (deletionCount + 1 > settings.MaxDeletionsPerRun)
            {
                var reason = $"Deletion guardrail exceeded. MaxDeletionsPerRun={settings.MaxDeletionsPerRun}.";
                return new WorkerRunResult(
                    WorkerId: user.WorkerId,
                    Bucket: "guardrailFailures",
                    SamAccountName: user.SamAccountName,
                    Reason: reason,
                    ReviewCategory: null,
                    ReviewCaseType: null,
                    Action: null,
                    Applied: false,
                    Succeeded: false,
                    OperationSummary: new OperationSummary("DeleteUser", "Deletion guardrail blocked execution.", null, user.CurrentOu, null),
                    DiffRows: [],
                    Item: BuildEntryItem(user, dryRun, "guardrailFailures", action: null, applied: false, succeeded: false, reason));
            }

            var action = "DeleteUser";
            var applied = false;
            var succeeded = true;
            var reasonMessage = dryRun
                ? $"Dry-run planned deletion for AD user {user.SamAccountName ?? user.WorkerId}."
                : $"Deleted AD user {user.SamAccountName ?? user.WorkerId}.";
            var bucket = "deletions";

            if (!dryRun)
            {
                try
                {
                    var result = await directoryCommandGateway.ExecuteAsync(
                        BuildDeleteCommand(user),
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
                WorkerId: user.WorkerId,
                Bucket: bucket,
                SamAccountName: user.SamAccountName,
                Reason: reasonMessage,
                ReviewCategory: null,
                ReviewCaseType: null,
                Action: action,
                Applied: applied,
                Succeeded: succeeded,
                OperationSummary: new OperationSummary(action, "The AD user object will be removed.", null, user.CurrentOu, null),
                DiffRows: [],
                Item: BuildEntryItem(user, dryRun, bucket, action, applied, succeeded, reasonMessage));
        }
        catch (GuardrailExceededException)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Delete-all planning failed. WorkerId={WorkerId}", user.WorkerId);
            return new WorkerRunResult(
                WorkerId: user.WorkerId,
                Bucket: "conflicts",
                SamAccountName: user.SamAccountName,
                Reason: ex.Message,
                ReviewCategory: "ExternalSystem",
                ReviewCaseType: "DeleteAllUsersFailed",
                Action: null,
                Applied: false,
                Succeeded: false,
                OperationSummary: null,
                DiffRows: [],
                Item: BuildEntryItem(user, dryRun, "conflicts", action: null, applied: false, succeeded: false, ex.Message));
        }
    }

    private static DirectoryMutationCommand BuildDeleteCommand(DeleteCandidate user)
    {
        return new DirectoryMutationCommand(
            Action: "DeleteUser",
            WorkerId: user.WorkerId,
            ManagerId: null,
            ManagerDistinguishedName: null,
            SamAccountName: user.SamAccountName ?? user.WorkerId,
            CommonName: user.SamAccountName ?? user.WorkerId,
            UserPrincipalName: user.Attributes.TryGetValue("UserPrincipalName", out var upn) ? upn ?? string.Empty : string.Empty,
            Mail: user.Attributes.TryGetValue("mail", out var mail) ? mail ?? string.Empty : string.Empty,
            TargetOu: user.CurrentOu ?? user.SourceOu,
            DisplayName: user.DisplayName ?? user.SamAccountName ?? user.WorkerId,
            CurrentDistinguishedName: user.DistinguishedName,
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
        DeleteCandidate user,
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
              "workerId": "{{Escape(user.WorkerId)}}",
              "samAccountName": {{ToJsonString(user.SamAccountName)}},
              "targetOu": {{ToJsonString(user.CurrentOu ?? user.SourceOu)}},
              "emplStatus": null,
              "currentOu": {{ToJsonString(user.CurrentOu)}},
              "managerDistinguishedName": null,
              "reviewCategory": null,
              "reviewCaseType": null,
              "reason": {{ToJsonString(reason)}},
              "bucket": "{{Escape(bucket)}}",
              "action": {{ToJsonString(action)}},
              "dryRun": {{(dryRun ? "true" : "false")}},
              "applied": {{(applied ? "true" : "false")}},
              "succeeded": {{(succeeded ? "true" : "false")}},
              "currentEnabled": {{ToJsonNullableBoolean(user.Enabled)}},
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

    private JsonElement BuildReport(string runId, RunQueueRequest request, RunTally tally, int totalWorkers, DateTimeOffset startedAt, IReadOnlyList<DeleteCandidate> users)
    {
        var targetOus = GetDeleteTargetOus();
        return ParseJson(
            $$"""
            {
              "kind": "deleteAllUsersRun",
              "syncScope": "Delete users from configured test OUs",
              "runId": "{{runId}}",
              "requestId": "{{request.RequestId}}",
              "mode": "{{request.Mode}}",
              "runTrigger": "{{request.RunTrigger}}",
              "requestedBy": {{ToJsonString(request.RequestedBy)}},
              "dryRun": {{(request.DryRun ? "true" : "false")}},
              "startedAt": "{{startedAt:O}}",
              "totalWorkers": {{totalWorkers}},
              "targetOus": [{{string.Join(",", targetOus.Select(ToJsonString))}}],
              "tally": {
                "deletions": {{tally.Deletions}},
                "guardrailFailures": {{tally.GuardrailFailures}},
                "conflicts": {{tally.Conflicts}},
                "unchanged": {{tally.Unchanged}}
              },
              "operations": [],
              "sampleUsers": [{{string.Join(",", users.Take(5).Select(user => $$"""
                {
                  "workerId": {{ToJsonString(user.WorkerId)}},
                  "samAccountName": {{ToJsonString(user.SamAccountName)}},
                  "distinguishedName": {{ToJsonString(user.DistinguishedName)}}
                }
                """))}}]
            }
            """);
    }

    private DeleteCandidate BuildDeleteCandidate(DirectoryUserSnapshot directoryUser, string sourceOu)
    {
        var workerId = ResolveWorkerId(directoryUser);
        return new DeleteCandidate(
            WorkerId: workerId,
            SamAccountName: directoryUser.SamAccountName,
            DistinguishedName: directoryUser.DistinguishedName,
            DisplayName: directoryUser.DisplayName,
            Enabled: directoryUser.Enabled,
            CurrentOu: DirectoryDistinguishedName.GetParentOu(directoryUser.DistinguishedName),
            SourceOu: sourceOu,
            Attributes: directoryUser.Attributes);
    }

    private string ResolveWorkerId(DirectoryUserSnapshot directoryUser)
    {
        if (directoryUser.Attributes.TryGetValue(lifecycleSettings.DirectoryIdentityAttribute, out var identityValue) &&
            !string.IsNullOrWhiteSpace(identityValue))
        {
            return identityValue.Trim();
        }

        if (!string.IsNullOrWhiteSpace(directoryUser.SamAccountName))
        {
            return directoryUser.SamAccountName;
        }

        if (!string.IsNullOrWhiteSpace(directoryUser.DistinguishedName))
        {
            return directoryUser.DistinguishedName;
        }

        return "unknown-directory-user";
    }

    private static string ResolveCandidateDeduplicationKey(DeleteCandidate candidate)
    {
        if (!string.IsNullOrWhiteSpace(candidate.DistinguishedName))
        {
            return candidate.DistinguishedName;
        }

        if (!string.IsNullOrWhiteSpace(candidate.SamAccountName))
        {
            return $"sam:{candidate.SamAccountName}";
        }

        return $"worker:{candidate.WorkerId}";
    }

    private IReadOnlyList<string> GetDeleteTargetOus()
    {
        return new[] { lifecycleSettings.ActiveOu, lifecycleSettings.PrehireOu, lifecycleSettings.GraveyardOu, lifecycleSettings.LeaveOu }
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
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

    private sealed record DeleteCandidate(
        string WorkerId,
        string? SamAccountName,
        string? DistinguishedName,
        string? DisplayName,
        bool? Enabled,
        string? CurrentOu,
        string SourceOu,
        IReadOnlyDictionary<string, string?> Attributes);
}

using SyncFactors.Contracts;
using Microsoft.Extensions.Logging;
using System.Text.Json;

namespace SyncFactors.Domain;

public interface IApplyPreviewService
{
    Task<DirectoryCommandResult> ApplyAsync(ApplyPreviewRequest request, CancellationToken cancellationToken);
}

public sealed class ApplyPreviewService(
    IWorkerSource workerSource,
    IDirectoryCommandGateway directoryCommandGateway,
    IRunRepository runRepository,
    IRuntimeStatusStore runtimeStatusStore,
    ILogger<ApplyPreviewService> logger) : IApplyPreviewService
{
    public async Task<DirectoryCommandResult> ApplyAsync(ApplyPreviewRequest request, CancellationToken cancellationToken)
    {
        logger.LogInformation("Starting preview apply flow. WorkerId={WorkerId} PreviewRunId={PreviewRunId}", request.WorkerId, request.PreviewRunId);
        var preview = await runRepository.GetWorkerPreviewAsync(request.PreviewRunId, cancellationToken);
        if (preview is null)
        {
            throw new InvalidOperationException($"Preview run {request.PreviewRunId} could not be resolved.");
        }

        if (!string.Equals(preview.WorkerId, request.WorkerId, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("The saved preview does not match the requested worker.");
        }

        if (!string.Equals(preview.Fingerprint, request.PreviewFingerprint, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("The saved preview no longer matches the reviewed preview snapshot. Refresh preview before applying.");
        }

        var requiredConfirmation = BuildConfirmationText(preview);
        if (!string.Equals(requiredConfirmation, request.ConfirmationText?.Trim(), StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"Confirmation text must exactly match '{requiredConfirmation}'.");
        }

        ValidatePreviewIsSafeToApply(preview);

        var worker = await workerSource.GetWorkerAsync(request.WorkerId, cancellationToken);
        if (worker is null)
        {
            logger.LogWarning("Apply preview could not resolve worker. WorkerId={WorkerId}", request.WorkerId);
            throw new InvalidOperationException($"Worker {request.WorkerId} could not be resolved.");
        }

        var samAccountName = preview.SamAccountName ?? throw new InvalidOperationException("Preview did not produce a SAM account name.");
        var displayName = GetPreviewAttributeValue(preview, "displayName")
            ?? samAccountName;
        var emailAddress = GetPreviewAttributeValue(preview, "UserPrincipalName")
            ?? GetPreviewAttributeValue(preview, "userPrincipalName")
            ?? GetPreviewAttributeValue(preview, "mail")
            ?? DirectoryIdentityFormatter.BuildEmailAddress(
                DirectoryIdentityFormatter.BuildBaseEmailLocalPart(worker.PreferredName, worker.LastName));
        var mailAddress = GetPreviewAttributeValue(preview, "mail") ?? emailAddress;
        var action = preview.Buckets.Contains("creates", StringComparer.OrdinalIgnoreCase) ? "CreateUser" : "UpdateUser";
        var command = new DirectoryMutationCommand(
            Action: action,
            WorkerId: worker.WorkerId,
            ManagerId: worker.Attributes.TryGetValue("managerId", out var managerId) ? managerId : null,
            ManagerDistinguishedName: preview.ManagerDistinguishedName,
            SamAccountName: samAccountName,
            UserPrincipalName: emailAddress,
            Mail: mailAddress,
            TargetOu: preview.TargetOu ?? worker.TargetOu,
            DisplayName: displayName,
            EnableAccount: preview.ProposedEnable ?? true,
            Attributes: BuildProposedAttributes(preview));
        logger.LogInformation("Prepared directory mutation command. WorkerId={WorkerId} Action={Action} SamAccountName={SamAccountName}", worker.WorkerId, action, command.SamAccountName);

        var startedAt = DateTimeOffset.UtcNow;
        var runId = $"apply-{worker.WorkerId}-{startedAt:yyyyMMddHHmmss}";

        await runtimeStatusStore.SaveAsync(
            new RuntimeStatus(
                Status: "InProgress",
                Stage: "ApplyPreview",
                RunId: runId,
                Mode: "ApplyPreview",
                DryRun: false,
                ProcessedWorkers: 0,
                TotalWorkers: 1,
                CurrentWorkerId: worker.WorkerId,
                LastAction: $"{action} for {command.SamAccountName} from preview {preview.RunId}",
                StartedAt: startedAt,
                LastUpdatedAt: startedAt,
                CompletedAt: null,
                ErrorMessage: null),
            cancellationToken);

        try
        {
            var result = await directoryCommandGateway.ExecuteAsync(command, cancellationToken);
            var completedAt = DateTimeOffset.UtcNow;
            logger.LogInformation("Directory mutation command finished. WorkerId={WorkerId} Action={Action} Succeeded={Succeeded} Message={Message}", worker.WorkerId, result.Action, result.Succeeded, result.Message);

            await PersistApplyOutcomeAsync(
                worker.WorkerId,
                preview,
                request,
                runId,
                action,
                startedAt,
                completedAt,
                result,
                cancellationToken);

            logger.LogInformation("Preview apply flow completed. WorkerId={WorkerId} RunId={RunId} Succeeded={Succeeded}", worker.WorkerId, runId, result.Succeeded);
            return result with { RunId = runId };
        }
        catch (Exception ex)
        {
            var completedAt = DateTimeOffset.UtcNow;
            logger.LogError(ex, "Directory mutation command failed. WorkerId={WorkerId} Action={Action}", worker.WorkerId, action);

            var failedResult = new DirectoryCommandResult(
                Succeeded: false,
                Action: action,
                SamAccountName: command.SamAccountName,
                DistinguishedName: null,
                Message: ex.Message,
                RunId: null);

            await PersistApplyOutcomeAsync(
                worker.WorkerId,
                preview,
                request,
                runId,
                action,
                startedAt,
                completedAt,
                failedResult,
                cancellationToken);

            throw;
        }
    }

    public static string BuildConfirmationText(WorkerPreviewResult preview)
    {
        var action = preview.Buckets.Contains("creates", StringComparer.OrdinalIgnoreCase) ? "CreateUser" : "UpdateUser";
        return $"APPLY {action} {preview.SamAccountName} FOR {preview.WorkerId}";
    }

    private static JsonElement ParseJson(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    private static IReadOnlyDictionary<string, string?> BuildProposedAttributes(WorkerPreviewResult preview)
    {
        var attributes = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);

        foreach (var row in preview.DiffRows.Where(row => row.Changed))
        {
            attributes[row.Attribute] = string.Equals(row.After, "(unset)", StringComparison.Ordinal)
                ? null
                : row.After;
        }

        return attributes;
    }

    private static string? GetPreviewAttributeValue(WorkerPreviewResult preview, string attributeName)
    {
        var row = preview.DiffRows.FirstOrDefault(diffRow =>
            string.Equals(diffRow.Attribute, attributeName, StringComparison.OrdinalIgnoreCase));
        if (row is null)
        {
            return null;
        }

        return string.Equals(row.After, "(unset)", StringComparison.Ordinal)
            ? null
            : row.After;
    }

    private static string Escape(string value)
    {
        var serialized = JsonSerializer.Serialize(value);
        return serialized[1..^1];
    }

    private async Task PersistApplyOutcomeAsync(
        string workerId,
        WorkerPreviewResult preview,
        ApplyPreviewRequest request,
        string runId,
        string action,
        DateTimeOffset startedAt,
        DateTimeOffset completedAt,
        DirectoryCommandResult result,
        CancellationToken cancellationToken)
    {
        var report = ParseJson(
            "{"
            + "\"kind\":\"applyPreview\","
            + $"\"workerId\":\"{Escape(workerId)}\","
            + $"\"action\":\"{Escape(result.Action)}\","
            + $"\"samAccountName\":\"{Escape(result.SamAccountName)}\","
            + $"\"succeeded\":{(result.Succeeded ? "true" : "false")},"
            + $"\"message\":\"{Escape(result.Message)}\","
            + $"\"sourcePreviewRunId\":\"{Escape(preview.RunId ?? request.PreviewRunId)}\","
            + $"\"sourcePreviewFingerprint\":\"{Escape(preview.Fingerprint)}\""
            + "}");

        await runRepository.SaveRunAsync(
            new RunRecord(
                RunId: runId,
                Path: null,
                ArtifactType: "ApplyPreview",
                ConfigPath: null,
                MappingConfigPath: null,
                Mode: "ApplyPreview",
                DryRun: false,
                Status: result.Succeeded ? "Succeeded" : "Failed",
                StartedAt: startedAt,
                CompletedAt: completedAt,
                DurationSeconds: Math.Max(0, (int)(completedAt - startedAt).TotalSeconds),
                Creates: action == "CreateUser" && result.Succeeded ? 1 : 0,
                Updates: action == "UpdateUser" && result.Succeeded ? 1 : 0,
                Enables: 0,
                Disables: 0,
                GraveyardMoves: 0,
                Deletions: 0,
                Quarantined: 0,
                Conflicts: 0,
                GuardrailFailures: 0,
                ManualReview: 0,
                Unchanged: 0,
                Report: report),
            cancellationToken);

        await runRepository.ReplaceRunEntriesAsync(
            runId,
            [
                new RunEntryRecord(
                    EntryId: $"{runId}:{workerId}:0",
                    RunId: runId,
                    Bucket: action == "CreateUser" ? "creates" : "updates",
                    BucketIndex: 0,
                    WorkerId: workerId,
                    SamAccountName: result.SamAccountName,
                    Reason: result.Message,
                    ReviewCategory: null,
                    ReviewCaseType: null,
                    StartedAt: startedAt,
                    Item: report)
            ],
            cancellationToken);

        await runtimeStatusStore.SaveAsync(
            new RuntimeStatus(
                Status: result.Succeeded ? "Idle" : "Failed",
                Stage: "Completed",
                RunId: runId,
                Mode: "ApplyPreview",
                DryRun: false,
                ProcessedWorkers: result.Succeeded ? 1 : 0,
                TotalWorkers: 1,
                CurrentWorkerId: null,
                LastAction: result.Message,
                StartedAt: startedAt,
                LastUpdatedAt: completedAt,
                CompletedAt: completedAt,
                ErrorMessage: result.Succeeded ? null : result.Message),
            cancellationToken);
    }

    private static void ValidatePreviewIsSafeToApply(WorkerPreviewResult preview)
    {
        if (!string.IsNullOrWhiteSpace(preview.ReviewCaseType))
        {
            throw new InvalidOperationException($"Preview requires review before apply. Review case: {preview.ReviewCaseType}.");
        }

        if (string.IsNullOrWhiteSpace(preview.SamAccountName))
        {
            throw new InvalidOperationException("Preview cannot be applied because the SAM account name is missing.");
        }

        var userPrincipalName = GetPreviewAttributeValue(preview, "UserPrincipalName")
            ?? GetPreviewAttributeValue(preview, "userPrincipalName")
            ?? GetPreviewAttributeValue(preview, "mail");
        if (string.IsNullOrWhiteSpace(userPrincipalName))
        {
            throw new InvalidOperationException("Preview cannot be applied because the planned email or user principal name is missing.");
        }

        var managerIdRequired = preview.Entries.Any(entry =>
            entry.Item.ValueKind == JsonValueKind.Object &&
            entry.Item.TryGetProperty("managerRequired", out var managerRequired) &&
            managerRequired.ValueKind == JsonValueKind.True);
        if (managerIdRequired && string.IsNullOrWhiteSpace(preview.ManagerDistinguishedName))
        {
            throw new InvalidOperationException("Preview cannot be applied because the manager could not be resolved in Active Directory.");
        }
    }
}

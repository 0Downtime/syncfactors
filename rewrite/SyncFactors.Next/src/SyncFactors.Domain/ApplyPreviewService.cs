using SyncFactors.Contracts;
using Microsoft.Extensions.Logging;
using System.Text.Json;

namespace SyncFactors.Domain;

public interface IApplyPreviewService
{
    Task<DirectoryCommandResult> ApplyAsync(string workerId, CancellationToken cancellationToken);
}

public sealed class ApplyPreviewService(
    IWorkerSource workerSource,
    IWorkerPreviewPlanner previewPlanner,
    IDirectoryCommandGateway directoryCommandGateway,
    IRunRepository runRepository,
    IRuntimeStatusStore runtimeStatusStore,
    ILogger<ApplyPreviewService> logger) : IApplyPreviewService
{
    public async Task<DirectoryCommandResult> ApplyAsync(string workerId, CancellationToken cancellationToken)
    {
        logger.LogInformation("Starting preview apply flow. WorkerId={WorkerId}", workerId);
        var worker = await workerSource.GetWorkerAsync(workerId, cancellationToken);
        if (worker is null)
        {
            logger.LogWarning("Apply preview could not resolve worker. WorkerId={WorkerId}", workerId);
            throw new InvalidOperationException($"Worker {workerId} could not be resolved.");
        }

        var preview = await previewPlanner.PreviewAsync(workerId, cancellationToken);
        var displayName = $"{worker.PreferredName} {worker.LastName}";
        var action = preview.Buckets.Contains("creates", StringComparer.OrdinalIgnoreCase) ? "CreateUser" : "UpdateUser";
        var command = new DirectoryMutationCommand(
            Action: action,
            WorkerId: worker.WorkerId,
            SamAccountName: preview.SamAccountName ?? throw new InvalidOperationException("Preview did not produce a SAM account name."),
            TargetOu: preview.TargetOu ?? worker.TargetOu,
            DisplayName: displayName,
            EnableAccount: preview.ProposedEnable ?? true);
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
                LastAction: $"{action} for {command.SamAccountName}",
                StartedAt: startedAt,
                LastUpdatedAt: startedAt,
                CompletedAt: null,
                ErrorMessage: null),
            cancellationToken);

        var result = await directoryCommandGateway.ExecuteAsync(command, cancellationToken);
        var completedAt = DateTimeOffset.UtcNow;
        logger.LogInformation("Directory mutation command finished. WorkerId={WorkerId} Action={Action} Succeeded={Succeeded} Message={Message}", worker.WorkerId, result.Action, result.Succeeded, result.Message);

        var report = ParseJson(
            "{"
            + "\"kind\":\"applyPreview\","
            + $"\"workerId\":\"{Escape(worker.WorkerId)}\","
            + $"\"action\":\"{Escape(result.Action)}\","
            + $"\"samAccountName\":\"{Escape(result.SamAccountName)}\","
            + $"\"succeeded\":{(result.Succeeded ? "true" : "false")},"
            + $"\"message\":\"{Escape(result.Message)}\""
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
                    EntryId: $"{runId}:{worker.WorkerId}:0",
                    RunId: runId,
                    Bucket: action == "CreateUser" ? "creates" : "updates",
                    BucketIndex: 0,
                    WorkerId: worker.WorkerId,
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

        logger.LogInformation("Preview apply flow completed. WorkerId={WorkerId} RunId={RunId} Succeeded={Succeeded}", worker.WorkerId, runId, result.Succeeded);
        return result with { RunId = runId };
    }

    private static JsonElement ParseJson(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    private static string Escape(string value)
    {
        return value.Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("\"", "\\\"", StringComparison.Ordinal);
    }
}

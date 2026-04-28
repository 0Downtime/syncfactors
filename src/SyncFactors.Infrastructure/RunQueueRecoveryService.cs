using Microsoft.Extensions.Logging;
using SyncFactors.Contracts;
using SyncFactors.Domain;

namespace SyncFactors.Infrastructure;

public sealed class RunQueueRecoveryService(
    IRunQueueStore runQueueStore,
    IRuntimeStatusStore runtimeStatusStore,
    IRunRepository runRepository,
    IWorkerHeartbeatStore workerHeartbeatStore,
    TimeProvider timeProvider,
    ILogger<RunQueueRecoveryService> logger)
{
    private static readonly TimeSpan FreshRunningHeartbeatAge = TimeSpan.FromMinutes(2);

    public async Task<int> RecoverIfNeededAsync(string trigger, CancellationToken cancellationToken, bool ignoreFreshHeartbeat = false)
    {
        var current = await runQueueStore.GetPendingOrActiveAsync(cancellationToken);
        var runtime = await runtimeStatusStore.GetCurrentAsync(cancellationToken);

        var hasRecoverableQueue = current is not null &&
            (string.Equals(current.Status, "InProgress", StringComparison.OrdinalIgnoreCase) ||
             string.Equals(current.Status, "CancelRequested", StringComparison.OrdinalIgnoreCase));
        var hasRecoverableRuntime = runtime is not null &&
            string.Equals(runtime.Status, "InProgress", StringComparison.OrdinalIgnoreCase);

        if (!hasRecoverableQueue && !hasRecoverableRuntime)
        {
            return 0;
        }

        var heartbeat = await workerHeartbeatStore.GetCurrentAsync(cancellationToken);
        var now = timeProvider.GetUtcNow();
        if (!ignoreFreshHeartbeat && heartbeat is not null)
        {
            var age = now - heartbeat.LastSeenAt;
            if (age <= FreshRunningHeartbeatAge &&
                string.Equals(heartbeat.State, "Running", StringComparison.OrdinalIgnoreCase))
            {
                logger.LogInformation(
                    "Skipping orphaned run recovery because a worker heartbeat is still active. RequestId={RequestId} Trigger={Trigger}",
                    current?.RequestId ?? runtime?.RunId ?? "unknown",
                    trigger);
                return 0;
            }
        }

        var errorMessage = heartbeat is null
            ? $"Recovered orphaned run during {trigger}: no worker heartbeat was present."
            : $"Recovered orphaned run during {trigger}: last worker heartbeat was {heartbeat.LastSeenAt:O} with state '{heartbeat.State}'.";
        var recovered = hasRecoverableQueue
            ? await runQueueStore.RecoverOrphanedActiveRunsAsync(errorMessage, cancellationToken)
            : 0;

        var terminalStatus = string.Equals(current?.Status, "CancelRequested", StringComparison.OrdinalIgnoreCase)
            ? "Canceled"
            : "Failed";
        var completedAt = now;
        var recoveredRunId = current?.RunId ?? runtime?.RunId;
        if (!string.IsNullOrWhiteSpace(recoveredRunId))
        {
            await RecoverRunAsync(recoveredRunId, terminalStatus, errorMessage, completedAt, cancellationToken);
        }

        if (hasRecoverableQueue || hasRecoverableRuntime)
        {
            await runtimeStatusStore.SaveAsync(
                BuildRecoveredRuntimeStatus(runtime, current, terminalStatus, errorMessage, completedAt),
                cancellationToken);
        }

        if (recovered > 0)
        {
            logger.LogWarning(
                "Recovered orphaned queued runs on startup. Count={Count} Trigger={Trigger}",
                recovered,
                trigger);
        }

        return recovered;
    }

    private async Task RecoverRunAsync(
        string runId,
        string terminalStatus,
        string errorMessage,
        DateTimeOffset completedAt,
        CancellationToken cancellationToken)
    {
        var run = await runRepository.GetRunAsync(runId, cancellationToken);
        if (run is null || !string.Equals(run.Run.Status, "InProgress", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        await runRepository.SaveRunAsync(
            new RunRecord(
                RunId: run.Run.RunId,
                Path: run.Run.Path,
                ArtifactType: run.Run.ArtifactType,
                ConfigPath: run.Run.ConfigPath,
                MappingConfigPath: run.Run.MappingConfigPath,
                Mode: run.Run.Mode,
                DryRun: run.Run.DryRun,
                Status: terminalStatus,
                StartedAt: run.Run.StartedAt,
                CompletedAt: completedAt,
                DurationSeconds: Math.Max(0, (int)(completedAt - run.Run.StartedAt).TotalSeconds),
                Creates: run.Run.Creates,
                Updates: run.Run.Updates,
                Enables: run.Run.Enables,
                Disables: run.Run.Disables,
                GraveyardMoves: run.Run.GraveyardMoves,
                Deletions: run.Run.Deletions,
                Quarantined: run.Run.Quarantined,
                Conflicts: run.Run.Conflicts,
                GuardrailFailures: run.Run.GuardrailFailures,
                ManualReview: run.Run.ManualReview,
                Unchanged: run.Run.Unchanged,
                Report: run.Report,
                RunTrigger: run.Run.RunTrigger,
                RequestedBy: run.Run.RequestedBy),
            cancellationToken);
    }

    private static RuntimeStatus BuildRecoveredRuntimeStatus(
        RuntimeStatus? runtime,
        RunQueueRequest? current,
        string terminalStatus,
        string errorMessage,
        DateTimeOffset completedAt)
    {
        var startedAt = runtime?.StartedAt;
        var mode = runtime?.Mode ?? current?.Mode;
        var dryRun = runtime?.DryRun ?? current?.DryRun ?? true;
        var totalWorkers = runtime?.TotalWorkers ?? 0;
        var processedWorkers = runtime?.ProcessedWorkers ?? 0;
        var runId = current?.RunId ?? runtime?.RunId;

        return string.Equals(terminalStatus, "Canceled", StringComparison.OrdinalIgnoreCase)
            ? new RuntimeStatus(
                Status: "Idle",
                Stage: "Canceled",
                RunId: runId,
                Mode: mode,
                DryRun: dryRun,
                ProcessedWorkers: 0,
                TotalWorkers: 0,
                CurrentWorkerId: null,
                LastAction: errorMessage,
                StartedAt: startedAt,
                LastUpdatedAt: completedAt,
                CompletedAt: completedAt,
                ErrorMessage: null)
            : new RuntimeStatus(
                Status: "Failed",
                Stage: mode ?? runtime?.Stage ?? "Recovered",
                RunId: runId,
                Mode: mode,
                DryRun: dryRun,
                ProcessedWorkers: processedWorkers,
                TotalWorkers: totalWorkers,
                CurrentWorkerId: null,
                LastAction: errorMessage,
                StartedAt: startedAt,
                LastUpdatedAt: completedAt,
                CompletedAt: completedAt,
                ErrorMessage: errorMessage);
    }
}

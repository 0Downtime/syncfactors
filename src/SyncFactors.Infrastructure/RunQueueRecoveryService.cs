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

        var terminalQueueRecovery = await TryRecoverQueueFromTerminalRuntimeAsync(current, runtime, trigger, cancellationToken);
        if (terminalQueueRecovery > 0)
        {
            return terminalQueueRecovery;
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

    private async Task<int> TryRecoverQueueFromTerminalRuntimeAsync(
        RunQueueRequest? current,
        RuntimeStatus? runtime,
        string trigger,
        CancellationToken cancellationToken)
    {
        if (current is null ||
            runtime is null ||
            !IsRecoverableQueueStatus(current.Status) ||
            string.IsNullOrWhiteSpace(runtime.RunId) ||
            !RuntimeBelongsToQueue(current, runtime))
        {
            return 0;
        }

        if (IsCompletedRuntime(runtime))
        {
            await runQueueStore.CompleteAsync(current.RequestId, runtime.RunId, cancellationToken);
            logger.LogWarning(
                "Recovered active queue row from completed runtime status. RequestId={RequestId} RunId={RunId} Trigger={Trigger}",
                current.RequestId,
                runtime.RunId,
                trigger);
            return 1;
        }

        if (IsCanceledRuntime(runtime))
        {
            await runQueueStore.CancelAsync(
                current.RequestId,
                runtime.RunId,
                runtime.LastAction ?? "Recovered queue row from canceled runtime status.",
                cancellationToken);
            logger.LogWarning(
                "Recovered active queue row from canceled runtime status. RequestId={RequestId} RunId={RunId} Trigger={Trigger}",
                current.RequestId,
                runtime.RunId,
                trigger);
            return 1;
        }

        return 0;
    }

    private static bool IsRecoverableQueueStatus(string status) =>
        string.Equals(status, "InProgress", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(status, "CancelRequested", StringComparison.OrdinalIgnoreCase);

    private static bool RuntimeBelongsToQueue(RunQueueRequest current, RuntimeStatus runtime)
    {
        if (!string.IsNullOrWhiteSpace(runtime.Mode) &&
            !string.Equals(current.Mode, runtime.Mode, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (!string.IsNullOrWhiteSpace(current.RunId))
        {
            return string.Equals(current.RunId, runtime.RunId, StringComparison.OrdinalIgnoreCase);
        }

        return runtime.StartedAt is not null &&
               current.StartedAt is not null &&
               runtime.StartedAt >= current.StartedAt.Value.AddSeconds(-5);
    }

    private static bool IsCompletedRuntime(RuntimeStatus runtime) =>
        runtime.CompletedAt is not null &&
        !string.IsNullOrWhiteSpace(runtime.RunId) &&
        string.Equals(runtime.Stage, "Completed", StringComparison.OrdinalIgnoreCase) &&
        (string.Equals(runtime.Status, "Idle", StringComparison.OrdinalIgnoreCase) ||
         string.Equals(runtime.Status, "Succeeded", StringComparison.OrdinalIgnoreCase));

    private static bool IsCanceledRuntime(RuntimeStatus runtime) =>
        runtime.CompletedAt is not null &&
        !string.IsNullOrWhiteSpace(runtime.RunId) &&
        string.Equals(runtime.Stage, "Canceled", StringComparison.OrdinalIgnoreCase) &&
        (string.Equals(runtime.Status, "Idle", StringComparison.OrdinalIgnoreCase) ||
         string.Equals(runtime.Status, "Canceled", StringComparison.OrdinalIgnoreCase));

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

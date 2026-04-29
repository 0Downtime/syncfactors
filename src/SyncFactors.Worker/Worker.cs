using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using SyncFactors.Contracts;
using SyncFactors.Domain;
using SyncFactors.Infrastructure;

public sealed class Worker(
    ILogger<Worker> logger,
    IRunQueueStore runQueueStore,
    SyncScheduleCoordinator syncScheduleCoordinator,
    GraveyardRetentionReportCoordinator graveyardRetentionReportCoordinator,
    GraveyardAutoDeleteCoordinator graveyardAutoDeleteCoordinator,
    BulkRunCoordinator bulkRunCoordinator,
    DeleteAllUsersCoordinator deleteAllUsersCoordinator,
    IWorkerHeartbeatStore workerHeartbeatStore,
    TimeProvider timeProvider,
    SyncFactorsConfigurationLoader configLoader) : BackgroundService
{
    private static readonly TimeSpan HeartbeatInterval = TimeSpan.FromSeconds(15);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("SyncFactors worker started.");
        var startedAt = timeProvider.GetUtcNow();
        await WriteHeartbeatAsync(startedAt, "Starting", "Worker process started.", stoppingToken);
        await WriteHeartbeatAsync(startedAt, "Idle", "Waiting for scheduled work.", stoppingToken);

        using var timer = new PeriodicTimer(HeartbeatInterval);
        do
        {
            await syncScheduleCoordinator.TryEnqueueDueRunAsync(stoppingToken);
            await graveyardRetentionReportCoordinator.TrySendDueReportAsync(stoppingToken);
            var claimed = await runQueueStore.ClaimNextPendingAsync("SyncFactors.Worker", stoppingToken);
            if (claimed is not null)
            {
                var activity = $"Executing queued run {claimed.RequestId}.";
                await WriteHeartbeatAsync(startedAt, "Running", activity, stoppingToken);
                using var heartbeatCts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
                var heartbeatTask = PumpHeartbeatsAsync(startedAt, "Running", activity, heartbeatCts.Token);
                try
                {
                    var maxDegreeOfParallelism = Math.Max(1, configLoader.GetSyncConfig().Sync.MaxDegreeOfParallelism);
                    var runId = string.Equals(claimed.Mode, "DeleteAllUsers", StringComparison.OrdinalIgnoreCase)
                        ? await deleteAllUsersCoordinator.ExecuteAsync(claimed, stoppingToken)
                        : await bulkRunCoordinator.ExecuteAsync(claimed, maxDegreeOfParallelism, stoppingToken);
                    await runQueueStore.CompleteAsync(claimed.RequestId, runId, CancellationToken.None);
                    await TryWriteHeartbeatAsync(startedAt, "Idle", $"Completed queued run {claimed.RequestId}.", CancellationToken.None);
                }
                catch (RunCanceledException ex)
                {
                    logger.LogInformation("Queued run canceled. RequestId={RequestId}", claimed.RequestId);
                    await runQueueStore.CancelAsync(claimed.RequestId, ex.RunId, ex.Message, CancellationToken.None);
                    await TryWriteHeartbeatAsync(startedAt, "Idle", $"Run {claimed.RequestId} canceled.", CancellationToken.None);
                }
                catch (OperationCanceledException ex) when (stoppingToken.IsCancellationRequested)
                {
                    logger.LogWarning(ex, "Worker stopped while a queued run was active. RequestId={RequestId}", claimed.RequestId);
                    await runQueueStore.FailAsync(claimed.RequestId, null, "Worker stopped while processing the queued run.", CancellationToken.None);
                    await TryWriteHeartbeatAsync(startedAt, "Stopping", $"Worker stopped while processing run {claimed.RequestId}.", CancellationToken.None);
                    throw;
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "Queued run failed. RequestId={RequestId}", claimed.RequestId);
                    var failedRunId = ex is GuardrailExceededException guardrailExceededException
                        ? guardrailExceededException.RunId
                        : null;
                    await runQueueStore.FailAsync(claimed.RequestId, failedRunId, ex.Message, CancellationToken.None);
                    await TryWriteHeartbeatAsync(startedAt, "Idle", $"Run {claimed.RequestId} failed.", CancellationToken.None);
                }
                finally
                {
                    heartbeatCts.Cancel();
                    await AwaitHeartbeatPumpAsync(heartbeatTask);
                }
            }
            else
            {
                await graveyardAutoDeleteCoordinator.TryExecuteAsync(stoppingToken);
                await WriteHeartbeatAsync(startedAt, "Idle", "Waiting for scheduled work.", stoppingToken);
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    private Task WriteHeartbeatAsync(
        DateTimeOffset startedAt,
        string state,
        string activity,
        CancellationToken cancellationToken)
    {
        return workerHeartbeatStore.SaveAsync(
            new WorkerHeartbeat(
                Service: "SyncFactors.Worker",
                State: state,
                Activity: activity,
                StartedAt: startedAt,
                LastSeenAt: timeProvider.GetUtcNow()),
            cancellationToken);
    }

    private async Task TryWriteHeartbeatAsync(
        DateTimeOffset startedAt,
        string state,
        string activity,
        CancellationToken cancellationToken)
    {
        try
        {
            await WriteHeartbeatAsync(startedAt, state, activity, cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to persist worker heartbeat. State={State} Activity={Activity}", state, activity);
        }
    }

    private async Task PumpHeartbeatsAsync(
        DateTimeOffset startedAt,
        string state,
        string activity,
        CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(HeartbeatInterval);
        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken))
            {
                try
                {
                    await WriteHeartbeatAsync(startedAt, state, activity, cancellationToken);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "Failed to persist worker heartbeat while processing a run.");
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }

    private static async Task AwaitHeartbeatPumpAsync(Task heartbeatTask)
    {
        try
        {
            await heartbeatTask;
        }
        catch (OperationCanceledException)
        {
        }
    }
}

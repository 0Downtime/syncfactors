using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using SyncFactors.Contracts;
using SyncFactors.Domain;
using SyncFactors.Infrastructure;


namespace SyncFactors.Worker;

public class Worker(
    ILogger<Worker> logger,
    IRunQueueStore runQueueStore,
    ISyncScheduleCoordinator syncScheduleCoordinator,
    IGraveyardRetentionReportCoordinator graveyardRetentionReportCoordinator,
    IGraveyardAutoDeleteCoordinator graveyardAutoDeleteCoordinator,
    IBulkRunCoordinator bulkRunCoordinator,
    IDeleteAllUsersCoordinator deleteAllUsersCoordinator,
    IWorkerHeartbeatStore workerHeartbeatStore,
    TimeProvider timeProvider,
    IWorkerExecutionSettings executionSettings,
    WorkerProcessIdentity processIdentity,
    WorkerDeploymentCommitGate deploymentCommitGate) : BackgroundService
{
    private static readonly TimeSpan HeartbeatInterval = TimeSpan.FromSeconds(15);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("SyncFactors worker started.");
        var startedAt = timeProvider.GetUtcNow();
        await WriteHeartbeatAsync(startedAt, "Starting", "Worker process started.", stoppingToken);

        if (deploymentCommitGate.IsRequired)
        {
            const string waitingActivity = "Waiting for deployment verification to commit this worker.";
            logger.LogInformation("Worker background processing is waiting for deployment verification.");
            await WriteHeartbeatAsync(startedAt, "Starting", waitingActivity, stoppingToken);
            using var heartbeatCts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
            var heartbeatTask = PumpHeartbeatsAsync(startedAt, "Starting", waitingActivity, heartbeatCts.Token);
            try
            {
                await deploymentCommitGate.WaitForCommitAsync(stoppingToken);
            }
            finally
            {
                await heartbeatCts.CancelAsync();
                await AwaitHeartbeatPumpAsync(heartbeatTask);
            }

            logger.LogInformation("Deployment verification committed; worker background processing is enabled.");
        }

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
                    var maxDegreeOfParallelism = Math.Max(1, executionSettings.GetMaxDegreeOfParallelism());
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
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    logger.LogWarning("Worker stopped while a queued run was active. RequestId={RequestId}", claimed.RequestId);
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
                    await heartbeatCts.CancelAsync();
                    await AwaitHeartbeatPumpAsync(heartbeatTask);
                }
            }
            else
            {
                try
                {
                    await graveyardAutoDeleteCoordinator.TryExecuteAsync(stoppingToken);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "Automatic graveyard deletion maintenance failed. The worker will remain available.");
                    await TryWriteHeartbeatAsync(
                        startedAt,
                        "Idle",
                        "Automatic graveyard deletion maintenance failed; waiting to retry.",
                        CancellationToken.None);
                }

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
                LastSeenAt: timeProvider.GetUtcNow(),
                InstanceId: processIdentity.InstanceId,
                BuildVersion: processIdentity.BuildVersion,
                BuildCommitSha: processIdentity.BuildCommitSha,
                DeploymentNonceHash: processIdentity.DeploymentNonceHash),
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

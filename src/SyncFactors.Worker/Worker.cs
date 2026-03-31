using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Configuration;
using SyncFactors.Contracts;
using SyncFactors.Domain;

public sealed class Worker(
    ILogger<Worker> logger,
    IRunQueueStore runQueueStore,
    SyncScheduleCoordinator syncScheduleCoordinator,
    BulkRunCoordinator bulkRunCoordinator,
    IWorkerHeartbeatStore workerHeartbeatStore,
    TimeProvider timeProvider,
    IConfiguration configuration) : BackgroundService
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
            var claimed = await runQueueStore.ClaimNextPendingAsync("SyncFactors.Worker", stoppingToken);
            if (claimed is not null)
            {
                await WriteHeartbeatAsync(startedAt, "Running", $"Executing queued run {claimed.RequestId}.", stoppingToken);
                try
                {
                    var maxDegreeOfParallelism = Math.Max(1, configuration.GetValue<int?>("SyncFactors:Worker:MaxDegreeOfParallelism") ?? 2);
                    var runId = await bulkRunCoordinator.ExecuteAsync(claimed, maxDegreeOfParallelism, stoppingToken);
                    await runQueueStore.CompleteAsync(claimed.RequestId, runId, stoppingToken);
                    await WriteHeartbeatAsync(startedAt, "Idle", $"Completed queued run {claimed.RequestId}.", stoppingToken);
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "Queued run failed. RequestId={RequestId}", claimed.RequestId);
                    await runQueueStore.FailAsync(claimed.RequestId, runId: null, ex.Message, stoppingToken);
                    await WriteHeartbeatAsync(startedAt, "Idle", $"Run {claimed.RequestId} failed.", stoppingToken);
                }
            }
            else
            {
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
}

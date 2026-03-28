using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using SyncFactors.Contracts;
using SyncFactors.Domain;

public sealed class Worker(
    ILogger<Worker> logger,
    IScaffoldRunPlanner scaffoldRunPlanner,
    IRunLifecycleService runLifecycleService,
    IWorkerHeartbeatStore workerHeartbeatStore,
    TimeProvider timeProvider) : BackgroundService
{
    private static readonly TimeSpan HeartbeatInterval = TimeSpan.FromSeconds(15);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("SyncFactors worker scaffold started.");
        var startedAt = timeProvider.GetUtcNow();
        await WriteHeartbeatAsync(startedAt, "Starting", "Worker process started.", stoppingToken);

        var plan = scaffoldRunPlanner.CreateBootstrapPlan(DateTimeOffset.UtcNow);
        await runLifecycleService.ExecutePlannedRunAsync(plan, stoppingToken);
        await WriteHeartbeatAsync(startedAt, "Idle", "Waiting for scheduled work.", stoppingToken);

        using var timer = new PeriodicTimer(HeartbeatInterval);
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            await WriteHeartbeatAsync(startedAt, "Idle", "Waiting for scheduled work.", stoppingToken);
        }
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

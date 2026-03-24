using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using SyncFactors.Domain;

public sealed class Worker(
    ILogger<Worker> logger,
    IScaffoldRunPlanner scaffoldRunPlanner,
    IRunLifecycleService runLifecycleService) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("SyncFactors worker scaffold started.");
        var plan = scaffoldRunPlanner.CreateBootstrapPlan(DateTimeOffset.UtcNow);
        await runLifecycleService.ExecutePlannedRunAsync(plan, stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            await Task.Delay(TimeSpan.FromMinutes(5), stoppingToken);
        }
    }
}

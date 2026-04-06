using Microsoft.Extensions.Logging;
using SyncFactors.Domain;

namespace SyncFactors.Infrastructure;

public sealed class RunQueueRecoveryService(
    IRunQueueStore runQueueStore,
    IWorkerHeartbeatStore workerHeartbeatStore,
    TimeProvider timeProvider,
    ILogger<RunQueueRecoveryService> logger)
{
    private static readonly TimeSpan FreshRunningHeartbeatAge = TimeSpan.FromMinutes(2);

    public async Task<int> RecoverIfNeededAsync(string trigger, CancellationToken cancellationToken)
    {
        var current = await runQueueStore.GetPendingOrActiveAsync(cancellationToken);
        if (current is null || !string.Equals(current.Status, "InProgress", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(current.Status, "CancelRequested", StringComparison.OrdinalIgnoreCase))
        {
            return 0;
        }

        var heartbeat = await workerHeartbeatStore.GetCurrentAsync(cancellationToken);
        var now = timeProvider.GetUtcNow();
        if (heartbeat is not null)
        {
            var age = now - heartbeat.LastSeenAt;
            if (age <= FreshRunningHeartbeatAge &&
                string.Equals(heartbeat.State, "Running", StringComparison.OrdinalIgnoreCase))
            {
                logger.LogInformation(
                    "Skipping orphaned run recovery because a worker heartbeat is still active. RequestId={RequestId} Trigger={Trigger}",
                    current.RequestId,
                    trigger);
                return 0;
            }
        }

        var errorMessage = heartbeat is null
            ? $"Recovered orphaned run during {trigger}: no worker heartbeat was present."
            : $"Recovered orphaned run during {trigger}: last worker heartbeat was {heartbeat.LastSeenAt:O} with state '{heartbeat.State}'.";
        var recovered = await runQueueStore.RecoverOrphanedActiveRunsAsync(errorMessage, cancellationToken);
        if (recovered > 0)
        {
            logger.LogWarning(
                "Recovered orphaned queued runs on startup. Count={Count} Trigger={Trigger}",
                recovered,
                trigger);
        }

        return recovered;
    }
}

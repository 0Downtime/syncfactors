using Microsoft.Extensions.Logging;
using SyncFactors.Contracts;

namespace SyncFactors.Domain;

public sealed class SyncScheduleCoordinator(
    ISyncScheduleStore scheduleStore,
    IRunQueueStore runQueueStore,
    RealSyncSettings realSyncSettings,
    TimeProvider timeProvider,
    ILogger<SyncScheduleCoordinator> logger)
{
    private static readonly TimeSpan FailedAttemptBackoff = TimeSpan.FromMinutes(1);

    public async Task<bool> TryEnqueueDueRunAsync(CancellationToken cancellationToken)
    {
        var schedule = await scheduleStore.GetCurrentAsync(cancellationToken);
        var now = timeProvider.GetUtcNow();

        if (!schedule.Enabled || schedule.NextRunAt is null || schedule.NextRunAt > now)
        {
            return false;
        }

        if (schedule.LastEnqueueAttemptAt is not null &&
            now - schedule.LastEnqueueAttemptAt.Value < FailedAttemptBackoff &&
            schedule.LastScheduledRunAt != schedule.LastEnqueueAttemptAt)
        {
            return false;
        }

        if (await runQueueStore.HasPendingOrActiveRunAsync(cancellationToken))
        {
            logger.LogDebug("Scheduled sync is due but another run is already pending or active.");
            return false;
        }

        try
        {
            await runQueueStore.EnqueueAsync(
                new StartRunRequest(
                    DryRun: !realSyncSettings.Enabled,
                    Mode: "BulkSync",
                    RunTrigger: "Scheduled",
                    RequestedBy: "Sync schedule"),
                cancellationToken);
            await scheduleStore.RecordSuccessfulEnqueueAsync(now, cancellationToken);
            return true;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to enqueue scheduled sync run.");
            await scheduleStore.RecordFailedEnqueueAsync(now, ex.Message, cancellationToken);
            return false;
        }
    }
}

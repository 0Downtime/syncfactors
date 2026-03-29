using SyncFactors.Contracts;

namespace SyncFactors.Domain;

public sealed class DashboardSnapshotService(
    IRuntimeStatusStore runtimeStatusStore,
    IRunRepository runRepository,
    TimeProvider timeProvider) : IDashboardSnapshotService
{
    public async Task<DashboardSnapshot> GetSnapshotAsync(CancellationToken cancellationToken)
    {
        var status = await runtimeStatusStore.GetCurrentAsync(cancellationToken)
            ?? new RuntimeStatus(
                Status: "Idle",
                Stage: "NotStarted",
                RunId: null,
                Mode: null,
                DryRun: true,
                ProcessedWorkers: 0,
                TotalWorkers: 0,
                CurrentWorkerId: null,
                LastAction: null,
                StartedAt: null,
                LastUpdatedAt: null,
                CompletedAt: null,
                ErrorMessage: null);

        var runs = await runRepository.ListRunsAsync(cancellationToken);
        var activeRun = runs.FirstOrDefault(run =>
            string.Equals(run.RunId, status.RunId, StringComparison.Ordinal) ||
            string.Equals(run.Status, "InProgress", StringComparison.OrdinalIgnoreCase));
        var lastCompletedRun = runs
            .Where(run => !string.Equals(run.Status, "InProgress", StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(run => run.CompletedAt ?? run.StartedAt)
            .FirstOrDefault();

        var requiresAttention = !string.IsNullOrWhiteSpace(status.ErrorMessage) ||
            string.Equals(status.Status, "Failed", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(lastCompletedRun?.Status, "Failed", StringComparison.OrdinalIgnoreCase);
        var attentionMessage = status.ErrorMessage;
        if (attentionMessage is null &&
            lastCompletedRun is not null &&
            string.Equals(lastCompletedRun.Status, "Failed", StringComparison.OrdinalIgnoreCase))
        {
            attentionMessage = $"Last completed run {lastCompletedRun.RunId} failed.";
        }

        return new DashboardSnapshot(
            Status: status,
            Runs: runs,
            ActiveRun: activeRun,
            LastCompletedRun: lastCompletedRun,
            RequiresAttention: requiresAttention,
            AttentionMessage: attentionMessage,
            CheckedAt: timeProvider.GetUtcNow());
    }
}

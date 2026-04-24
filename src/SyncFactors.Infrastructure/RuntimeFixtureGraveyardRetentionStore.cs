using SyncFactors.Contracts;
using SyncFactors.Domain;

namespace SyncFactors.Infrastructure;

public sealed class RuntimeFixtureGraveyardRetentionStore(
    MockRuntimeFixtureReader fixtureReader,
    SqliteGraveyardRetentionStore sqliteStore,
    LifecyclePolicySettings lifecycleSettings,
    TimeProvider timeProvider) : IGraveyardRetentionStore
{
    public Task UpsertObservedAsync(GraveyardRetentionRecord record, CancellationToken cancellationToken) =>
        sqliteStore.UpsertObservedAsync(record, cancellationToken);

    public Task ResolveAsync(string workerId, CancellationToken cancellationToken) =>
        sqliteStore.ResolveAsync(workerId, cancellationToken);

    public async Task<IReadOnlyList<GraveyardRetentionRecord>> ListActiveAsync(CancellationToken cancellationToken)
    {
        var overlays = await sqliteStore.ListActiveAsync(cancellationToken);
        var overlaysByWorkerId = overlays.ToDictionary(record => record.WorkerId, StringComparer.OrdinalIgnoreCase);
        var now = timeProvider.GetUtcNow();

        var synthetic = fixtureReader.ListWorkers()
            .Where(worker => MockRuntimeFixtureProjection.IsGraveyard(worker, lifecycleSettings))
            .Select(worker => BuildRecord(worker, now))
            .Where(record => !string.IsNullOrWhiteSpace(record.WorkerId))
            .Select(record => overlaysByWorkerId.TryGetValue(record.WorkerId, out var overlay)
                ? record with
                {
                    IsOnHold = overlay.IsOnHold,
                    HoldPlacedAtUtc = overlay.HoldPlacedAtUtc,
                    HoldPlacedBy = overlay.HoldPlacedBy
                }
                : record)
            .ToArray();

        return synthetic;
    }

    public async Task SetHoldAsync(string workerId, bool isOnHold, string? actingUserId, DateTimeOffset changedAtUtc, CancellationToken cancellationToken)
    {
        var fixture = fixtureReader.ListWorkers()
            .FirstOrDefault(candidate => MockRuntimeFixtureProjection.MatchesWorkerId(candidate, workerId));

        if (fixture is not null)
        {
            await sqliteStore.UpsertObservedAsync(BuildRecord(fixture, timeProvider.GetUtcNow()), cancellationToken);
        }

        await sqliteStore.SetHoldAsync(workerId, isOnHold, actingUserId, changedAtUtc, cancellationToken);
    }

    public Task<GraveyardRetentionReportStatus> GetReportStatusAsync(CancellationToken cancellationToken) =>
        sqliteStore.GetReportStatusAsync(cancellationToken);

    public Task RecordReportAttemptAsync(DateTimeOffset attemptedAt, string? error, DateTimeOffset? sentAtUtc, CancellationToken cancellationToken) =>
        sqliteStore.RecordReportAttemptAsync(attemptedAt, error, sentAtUtc, cancellationToken);

    private GraveyardRetentionRecord BuildRecord(MockRuntimeFixtureWorkerRecord worker, DateTimeOffset now)
    {
        var workerId = MockRuntimeFixtureProjection.ResolveWorkerId(worker);
        var samAccountName = MockRuntimeFixtureProjection.ResolveSamAccountName(worker);
        var displayName = MockRuntimeFixtureProjection.ResolveDisplayName(worker);
        var distinguishedName = MockRuntimeFixtureProjection.BuildDistinguishedName(worker, lifecycleSettings, now);
        var anchorDate = MockRuntimeFixtureProjection.ResolveAnchorDate(worker);

        return new GraveyardRetentionRecord(
            WorkerId: workerId,
            SamAccountName: string.IsNullOrWhiteSpace(samAccountName) ? null : samAccountName,
            DisplayName: displayName,
            DistinguishedName: distinguishedName,
            Status: worker.EmploymentStatus ?? string.Empty,
            EndDateUtc: anchorDate,
            LastObservedAtUtc: now,
            Active: true);
    }
}

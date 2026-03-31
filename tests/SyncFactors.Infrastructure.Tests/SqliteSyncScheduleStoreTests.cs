using SyncFactors.Contracts;
using SyncFactors.Domain;
using SyncFactors.Infrastructure;

namespace SyncFactors.Infrastructure.Tests;

public sealed class SqliteSyncScheduleStoreTests
{
    [Fact]
    public async Task GetCurrentAsync_ReturnsDisabledDefaultSchedule_WhenUnset()
    {
        var pathResolver = await CreateInitializedDatabaseAsync();
        ISyncScheduleStore store = new SqliteSyncScheduleStore(pathResolver, TimeProvider.System);

        var schedule = await store.GetCurrentAsync(CancellationToken.None);

        Assert.False(schedule.Enabled);
        Assert.Equal(30, schedule.IntervalMinutes);
        Assert.Null(schedule.NextRunAt);
    }

    [Fact]
    public async Task UpdateAsync_AndRecordSuccessfulEnqueue_PersistScheduleState()
    {
        var pathResolver = await CreateInitializedDatabaseAsync();
        var timeProvider = new FakeTimeProvider(DateTimeOffset.Parse("2026-03-30T12:00:00Z"));
        ISyncScheduleStore store = new SqliteSyncScheduleStore(pathResolver, timeProvider);

        var updated = await store.UpdateAsync(new UpdateSyncScheduleRequest(true, 45), CancellationToken.None);
        var success = await store.RecordSuccessfulEnqueueAsync(DateTimeOffset.Parse("2026-03-30T12:45:00Z"), CancellationToken.None);

        Assert.True(updated.Enabled);
        Assert.Equal(45, updated.IntervalMinutes);
        Assert.Equal(DateTimeOffset.Parse("2026-03-30T12:45:00Z"), updated.NextRunAt);
        Assert.Equal(DateTimeOffset.Parse("2026-03-30T13:30:00Z"), success.NextRunAt);
        Assert.Equal(DateTimeOffset.Parse("2026-03-30T12:45:00Z"), success.LastScheduledRunAt);
    }

    [Fact]
    public async Task RecordFailedEnqueueAsync_PersistsLastError()
    {
        var pathResolver = await CreateInitializedDatabaseAsync();
        var timeProvider = new FakeTimeProvider(DateTimeOffset.Parse("2026-03-30T12:00:00Z"));
        ISyncScheduleStore store = new SqliteSyncScheduleStore(pathResolver, timeProvider);
        await store.UpdateAsync(new UpdateSyncScheduleRequest(true, 30), CancellationToken.None);

        var failed = await store.RecordFailedEnqueueAsync(DateTimeOffset.Parse("2026-03-30T12:30:00Z"), "boom", CancellationToken.None);

        Assert.Equal("boom", failed.LastEnqueueError);
        Assert.Equal(DateTimeOffset.Parse("2026-03-30T12:30:00Z"), failed.LastEnqueueAttemptAt);
    }

    private static async Task<SqlitePathResolver> CreateInitializedDatabaseAsync()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "syncfactors-sync-schedule", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);
        var databasePath = Path.Combine(tempRoot, "runtime.db");
        var pathResolver = new SqlitePathResolver(databasePath);
        var initializer = new SqliteDatabaseInitializer(pathResolver);
        await initializer.InitializeAsync(CancellationToken.None);
        return pathResolver;
    }

    private sealed class FakeTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => utcNow;
    }
}

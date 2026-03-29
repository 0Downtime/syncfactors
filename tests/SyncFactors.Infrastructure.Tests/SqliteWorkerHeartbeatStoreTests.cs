using SyncFactors.Contracts;
using SyncFactors.Domain;
using SyncFactors.Infrastructure;

namespace SyncFactors.Infrastructure.Tests;

public sealed class SqliteWorkerHeartbeatStoreTests
{
    [Fact]
    public async Task SaveAsync_ThenGetCurrentAsync_RoundTripsHeartbeat()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "syncfactors-worker-heartbeat", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);

        var databasePath = Path.Combine(tempRoot, "runtime.db");
        var pathResolver = new SqlitePathResolver(databasePath);
        var initializer = new SqliteDatabaseInitializer(pathResolver);
        await initializer.InitializeAsync(CancellationToken.None);

        IWorkerHeartbeatStore store = new SqliteWorkerHeartbeatStore(pathResolver);
        var heartbeat = new WorkerHeartbeat(
            Service: "SyncFactors.Worker",
            State: "Idle",
            Activity: "Waiting for scheduled work.",
            StartedAt: DateTimeOffset.Parse("2026-03-27T12:00:00Z"),
            LastSeenAt: DateTimeOffset.Parse("2026-03-27T12:00:15Z"));

        await store.SaveAsync(heartbeat, CancellationToken.None);
        var current = await store.GetCurrentAsync(CancellationToken.None);

        Assert.NotNull(current);
        Assert.Equal("SyncFactors.Worker", current!.Service);
        Assert.Equal("Idle", current.State);
        Assert.Equal("Waiting for scheduled work.", current.Activity);
        Assert.Equal(heartbeat.StartedAt, current.StartedAt);
        Assert.Equal(heartbeat.LastSeenAt, current.LastSeenAt);
    }

    [Fact]
    public async Task GetCurrentAsync_ReturnsNull_WhenNoHeartbeatExists()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "syncfactors-worker-heartbeat", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);

        var databasePath = Path.Combine(tempRoot, "runtime.db");
        var pathResolver = new SqlitePathResolver(databasePath);
        var initializer = new SqliteDatabaseInitializer(pathResolver);
        await initializer.InitializeAsync(CancellationToken.None);

        IWorkerHeartbeatStore store = new SqliteWorkerHeartbeatStore(pathResolver);

        var current = await store.GetCurrentAsync(CancellationToken.None);

        Assert.Null(current);
    }
}

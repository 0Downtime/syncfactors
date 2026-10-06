using Microsoft.Data.Sqlite;
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
            LastSeenAt: DateTimeOffset.Parse("2026-03-27T12:00:15Z"),
            InstanceId: "worker-instance-123",
            BuildVersion: "1.2.3+sha.0123456789abcdef",
            BuildCommitSha: "0123456789abcdef",
            DeploymentNonceHash: new string('a', 64));

        await store.SaveAsync(heartbeat, CancellationToken.None);
        var current = await store.GetCurrentAsync(CancellationToken.None);

        Assert.NotNull(current);
        Assert.Equal("SyncFactors.Worker", current!.Service);
        Assert.Equal("Idle", current.State);
        Assert.Equal("Waiting for scheduled work.", current.Activity);
        Assert.Equal(heartbeat.StartedAt, current.StartedAt);
        Assert.Equal(heartbeat.LastSeenAt, current.LastSeenAt);
        Assert.Equal(heartbeat.InstanceId, current.InstanceId);
        Assert.Equal(heartbeat.BuildVersion, current.BuildVersion);
        Assert.Equal(heartbeat.BuildCommitSha, current.BuildCommitSha);
        Assert.Equal(heartbeat.DeploymentNonceHash, current.DeploymentNonceHash);
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

    [Fact]
    public async Task InitializeAsync_ConfiguresRuntimeDatabaseForWalJournalMode()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "syncfactors-worker-heartbeat", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);

        var databasePath = Path.Combine(tempRoot, "runtime.db");
        var pathResolver = new SqlitePathResolver(databasePath);
        var initializer = new SqliteDatabaseInitializer(pathResolver);
        await initializer.InitializeAsync(CancellationToken.None);

        await using var connection = new SqliteConnection($"Data Source={databasePath};Mode=ReadOnly");
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA journal_mode;";

        var journalMode = await command.ExecuteScalarAsync();

        Assert.Equal("wal", Assert.IsType<string>(journalMode), ignoreCase: true);
    }
}

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
    public async Task SaveAsync_UpdatesCurrentHeartbeat_InSingleServiceRow()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "syncfactors-worker-heartbeat", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);

        var databasePath = Path.Combine(tempRoot, "runtime.db");
        var pathResolver = new SqlitePathResolver(databasePath);
        var initializer = new SqliteDatabaseInitializer(pathResolver);
        await initializer.InitializeAsync(CancellationToken.None);

        IWorkerHeartbeatStore store = new SqliteWorkerHeartbeatStore(pathResolver);
        var startedAt = DateTimeOffset.Parse("2026-03-27T12:00:00Z");

        await store.SaveAsync(
            new WorkerHeartbeat(
                Service: "SyncFactors.Worker",
                State: "Running",
                Activity: "Processing queued run.",
                StartedAt: startedAt,
                LastSeenAt: DateTimeOffset.Parse("2026-03-27T12:00:15Z")),
            CancellationToken.None);

        await store.SaveAsync(
            new WorkerHeartbeat(
                Service: "SyncFactors.Worker",
                State: "Idle",
                Activity: "Waiting for scheduled work.",
                StartedAt: startedAt,
                LastSeenAt: DateTimeOffset.Parse("2026-03-27T12:00:30Z")),
            CancellationToken.None);

        await using var connection = new SqliteConnection($"Data Source={databasePath};Mode=ReadOnly");
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM worker_heartbeat;";

        var count = await command.ExecuteScalarAsync();
        var current = await store.GetCurrentAsync(CancellationToken.None);

        Assert.Equal(1L, count);
        Assert.NotNull(current);
        Assert.Equal("Idle", current!.State);
        Assert.Equal(DateTimeOffset.Parse("2026-03-27T12:00:30Z"), current.LastSeenAt);
    }

    [Fact]
    public async Task SaveAsync_Retries_WhenDatabaseWriterIsTemporarilyLocked()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "syncfactors-worker-heartbeat", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);

        var databasePath = Path.Combine(tempRoot, "runtime.db");
        var pathResolver = new SqlitePathResolver(databasePath);
        var initializer = new SqliteDatabaseInitializer(pathResolver);
        await initializer.InitializeAsync(CancellationToken.None);

        await using var lockConnection = new SqliteConnection($"Data Source={databasePath};Mode=ReadWrite");
        await lockConnection.OpenAsync();
        await using var lockCommand = lockConnection.CreateCommand();
        lockCommand.CommandText = "BEGIN IMMEDIATE;";
        await lockCommand.ExecuteNonQueryAsync();

        IWorkerHeartbeatStore store = new SqliteWorkerHeartbeatStore(pathResolver);
        var heartbeat = new WorkerHeartbeat(
            Service: "SyncFactors.Worker",
            State: "Running",
            Activity: "Processing queued run.",
            StartedAt: DateTimeOffset.Parse("2026-03-27T12:00:00Z"),
            LastSeenAt: DateTimeOffset.Parse("2026-03-27T12:00:15Z"));

        var saveTask = store.SaveAsync(heartbeat, CancellationToken.None);
        await Task.Delay(TimeSpan.FromSeconds(6));
        await using (var releaseCommand = lockConnection.CreateCommand())
        {
            releaseCommand.CommandText = "COMMIT;";
            await releaseCommand.ExecuteNonQueryAsync();
        }

        await saveTask;
        var current = await store.GetCurrentAsync(CancellationToken.None);

        Assert.NotNull(current);
        Assert.Equal("Running", current!.State);
        Assert.Equal("Processing queued run.", current.Activity);
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

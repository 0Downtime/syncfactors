using Microsoft.Data.Sqlite;
using SyncFactors.Contracts;
using SyncFactors.Infrastructure;

namespace SyncFactors.Infrastructure.Tests;

public sealed class SqliteRunQueueStoreTests
{
    [Fact]
    public async Task EnqueueAsync_DefaultsBlankModeAndTrigger()
    {
        var databasePath = await CreateDatabaseAsync();

        try
        {
            var store = new SqliteRunQueueStore(new SqlitePathResolver(databasePath));

            var queued = await store.EnqueueAsync(
                new StartRunRequest(DryRun: true, Mode: " ", RunTrigger: "", RequestedBy: null),
                CancellationToken.None);

            Assert.Equal("BulkSync", queued.Mode);
            Assert.True(queued.DryRun);
            Assert.Equal("AdHoc", queued.RunTrigger);
            Assert.Equal("Pending", queued.Status);

            var persisted = await store.GetAsync(queued.RequestId, CancellationToken.None);
            Assert.NotNull(persisted);
            Assert.Equal("BulkSync", persisted!.Mode);
            Assert.Equal("AdHoc", persisted.RunTrigger);
            Assert.Null(persisted.RequestedBy);
        }
        finally
        {
            File.Delete(databasePath);
        }
    }

    [Fact]
    public async Task ClaimNextPendingAsync_ClaimsOldestPendingRequest()
    {
        var databasePath = await CreateDatabaseAsync();

        try
        {
            await ExecuteAsync(
                databasePath,
                """
                INSERT INTO run_queue (
                  request_id, mode, dry_run, run_trigger, requested_by, status, requested_at, started_at, completed_at, run_id, worker_name, error_message
                )
                VALUES
                  ('req-new', 'BulkSync', 1, 'AdHoc', 'newer', 'Pending', '2026-04-06T12:05:00Z', NULL, NULL, NULL, NULL, NULL),
                  ('req-old', 'BulkSync', 0, 'Scheduled', 'older', 'Pending', '2026-04-06T12:00:00Z', NULL, NULL, NULL, NULL, NULL);
                """);

            var store = new SqliteRunQueueStore(new SqlitePathResolver(databasePath));

            var claimed = await store.ClaimNextPendingAsync("worker-a", CancellationToken.None);

            Assert.NotNull(claimed);
            Assert.Equal("req-old", claimed!.RequestId);
            Assert.Equal("InProgress", claimed.Status);
            Assert.NotNull(claimed.StartedAt);

            var persisted = await store.GetAsync("req-old", CancellationToken.None);
            Assert.Equal("InProgress", persisted!.Status);
            Assert.NotNull(persisted.StartedAt);
        }
        finally
        {
            File.Delete(databasePath);
        }
    }

    [Fact]
    public async Task ClaimNextPendingAsync_ReturnsNull_WhenActiveRunExistsOrNoPendingRowsExist()
    {
        var databasePath = await CreateDatabaseAsync();

        try
        {
            await ExecuteAsync(
                databasePath,
                """
                INSERT INTO run_queue (
                  request_id, mode, dry_run, run_trigger, requested_by, status, requested_at, started_at, completed_at, run_id, worker_name, error_message
                )
                VALUES
                  ('req-active', 'BulkSync', 1, 'AdHoc', 'test', 'InProgress', '2026-04-06T12:00:00Z', '2026-04-06T12:01:00Z', NULL, NULL, 'worker-a', NULL),
                  ('req-pending', 'BulkSync', 1, 'AdHoc', 'test', 'Pending', '2026-04-06T12:02:00Z', NULL, NULL, NULL, NULL, NULL);
                """);

            var store = new SqliteRunQueueStore(new SqlitePathResolver(databasePath));

            Assert.Null(await store.ClaimNextPendingAsync("worker-b", CancellationToken.None));

            await ExecuteAsync(databasePath, "UPDATE run_queue SET status = 'Completed', completed_at = '2026-04-06T12:03:00Z';");
            Assert.Null(await store.ClaimNextPendingAsync("worker-b", CancellationToken.None));
        }
        finally
        {
            File.Delete(databasePath);
        }
    }

    [Fact]
    public async Task GetPendingOrActiveAsync_PrioritizesActiveThenCancelRequestedThenPending()
    {
        var databasePath = await CreateDatabaseAsync();

        try
        {
            await ExecuteAsync(
                databasePath,
                """
                INSERT INTO run_queue (
                  request_id, mode, dry_run, run_trigger, requested_by, status, requested_at, started_at, completed_at, run_id, worker_name, error_message
                )
                VALUES
                  ('req-pending', 'BulkSync', 1, 'AdHoc', 'test', 'Pending', '2026-04-06T12:00:00Z', NULL, NULL, NULL, NULL, NULL),
                  ('req-cancel', 'BulkSync', 1, 'AdHoc', 'test', 'CancelRequested', '2026-04-06T12:01:00Z', '2026-04-06T12:02:00Z', NULL, NULL, 'worker-a', 'cancel'),
                  ('req-active', 'BulkSync', 1, 'AdHoc', 'test', 'InProgress', '2026-04-06T12:02:00Z', '2026-04-06T12:03:00Z', NULL, NULL, 'worker-b', NULL);
                """);

            var store = new SqliteRunQueueStore(new SqlitePathResolver(databasePath));

            var active = await store.GetPendingOrActiveAsync(CancellationToken.None);
            Assert.Equal("req-active", active!.RequestId);
            Assert.True(await store.HasPendingOrActiveRunAsync(CancellationToken.None));

            await ExecuteAsync(databasePath, "UPDATE run_queue SET status = 'Completed', completed_at = '2026-04-06T12:04:00Z' WHERE request_id = 'req-active';");
            var cancelRequested = await store.GetPendingOrActiveAsync(CancellationToken.None);
            Assert.Equal("req-cancel", cancelRequested!.RequestId);

            await ExecuteAsync(databasePath, "UPDATE run_queue SET status = 'Canceled', completed_at = '2026-04-06T12:05:00Z' WHERE request_id = 'req-cancel';");
            var pending = await store.GetPendingOrActiveAsync(CancellationToken.None);
            Assert.Equal("req-pending", pending!.RequestId);
        }
        finally
        {
            File.Delete(databasePath);
        }
    }

    [Fact]
    public async Task CancelPendingOrActiveAsync_CancelsPendingImmediatelyAndMarksActiveForCancellation()
    {
        var databasePath = await CreateDatabaseAsync();

        try
        {
            await ExecuteAsync(
                databasePath,
                """
                INSERT INTO run_queue (
                  request_id, mode, dry_run, run_trigger, requested_by, status, requested_at, started_at, completed_at, run_id, worker_name, error_message
                )
                VALUES
                  ('req-pending', 'BulkSync', 1, 'AdHoc', 'test', 'Pending', '2026-04-06T12:00:00Z', NULL, NULL, NULL, NULL, NULL);
                """);

            var store = new SqliteRunQueueStore(new SqlitePathResolver(databasePath));

            Assert.True(await store.CancelPendingOrActiveAsync(null, CancellationToken.None));
            var canceledPending = await store.GetAsync("req-pending", CancellationToken.None);
            Assert.Equal("Canceled", canceledPending!.Status);
            Assert.Equal("Cancellation requested.", canceledPending.ErrorMessage);
            Assert.NotNull(canceledPending.CompletedAt);

            await ExecuteAsync(
                databasePath,
                """
                INSERT INTO run_queue (
                  request_id, mode, dry_run, run_trigger, requested_by, status, requested_at, started_at, completed_at, run_id, worker_name, error_message
                )
                VALUES
                  ('req-active', 'BulkSync', 1, 'AdHoc', 'test', 'InProgress', '2026-04-06T12:10:00Z', '2026-04-06T12:11:00Z', NULL, NULL, 'worker-a', NULL);
                """);

            Assert.True(await store.CancelPendingOrActiveAsync("operator", CancellationToken.None));
            var cancelRequested = await store.GetAsync("req-active", CancellationToken.None);
            Assert.Equal("CancelRequested", cancelRequested!.Status);
            Assert.Equal("Cancellation requested by operator.", cancelRequested.ErrorMessage);
            Assert.Null(cancelRequested.CompletedAt);

            await ExecuteAsync(databasePath, "UPDATE run_queue SET status = 'Completed', completed_at = '2026-04-06T12:12:00Z' WHERE request_id = 'req-active';");
            Assert.False(await store.CancelPendingOrActiveAsync("operator", CancellationToken.None));
        }
        finally
        {
            File.Delete(databasePath);
        }
    }

    [Fact]
    public async Task IsCancellationRequestedAsync_ReturnsTrueOnlyForCancelRequested()
    {
        var databasePath = await CreateDatabaseAsync();

        try
        {
            await ExecuteAsync(
                databasePath,
                """
                INSERT INTO run_queue (
                  request_id, mode, dry_run, run_trigger, requested_by, status, requested_at, started_at, completed_at, run_id, worker_name, error_message
                )
                VALUES
                  ('req-cancel', 'BulkSync', 1, 'AdHoc', 'test', 'CancelRequested', '2026-04-06T12:00:00Z', NULL, NULL, NULL, NULL, NULL),
                  ('req-active', 'BulkSync', 1, 'AdHoc', 'test', 'InProgress', '2026-04-06T12:00:00Z', NULL, NULL, NULL, NULL, NULL);
                """);

            var store = new SqliteRunQueueStore(new SqlitePathResolver(databasePath));

            Assert.True(await store.IsCancellationRequestedAsync("req-cancel", CancellationToken.None));
            Assert.False(await store.IsCancellationRequestedAsync("req-active", CancellationToken.None));
            Assert.False(await store.IsCancellationRequestedAsync("missing", CancellationToken.None));
        }
        finally
        {
            File.Delete(databasePath);
        }
    }

    [Fact]
    public async Task TerminalUpdates_SetStatusRunIdAndErrorMessage()
    {
        var databasePath = await CreateDatabaseAsync();

        try
        {
            await ExecuteAsync(
                databasePath,
                """
                INSERT INTO run_queue (
                  request_id, mode, dry_run, run_trigger, requested_by, status, requested_at, started_at, completed_at, run_id, worker_name, error_message
                )
                VALUES
                  ('req-complete', 'BulkSync', 1, 'AdHoc', 'test', 'InProgress', '2026-04-06T12:00:00Z', NULL, NULL, NULL, NULL, NULL),
                  ('req-cancel', 'BulkSync', 1, 'AdHoc', 'test', 'CancelRequested', '2026-04-06T12:01:00Z', NULL, NULL, NULL, NULL, NULL),
                  ('req-fail', 'BulkSync', 1, 'AdHoc', 'test', 'InProgress', '2026-04-06T12:02:00Z', NULL, NULL, NULL, NULL, NULL);
                """);

            var store = new SqliteRunQueueStore(new SqlitePathResolver(databasePath));

            await store.CompleteAsync("req-complete", "run-1", CancellationToken.None);
            await store.CancelAsync("req-cancel", null, "Stopped.", CancellationToken.None);
            await store.FailAsync("req-fail", "run-3", "Failed.", CancellationToken.None);

            var completed = await store.GetAsync("req-complete", CancellationToken.None);
            Assert.Equal("Completed", completed!.Status);
            Assert.Equal("run-1", completed.RunId);
            Assert.Null(completed.ErrorMessage);

            var canceled = await store.GetAsync("req-cancel", CancellationToken.None);
            Assert.Equal("Canceled", canceled!.Status);
            Assert.Null(canceled.RunId);
            Assert.Equal("Stopped.", canceled.ErrorMessage);

            var failed = await store.GetAsync("req-fail", CancellationToken.None);
            Assert.Equal("Failed", failed!.Status);
            Assert.Equal("run-3", failed.RunId);
            Assert.Equal("Failed.", failed.ErrorMessage);
        }
        finally
        {
            File.Delete(databasePath);
        }
    }

    [Fact]
    public async Task SeedRecoveryProbeAsync_ValidatesStatusAndDefaultsBlankFields()
    {
        var databasePath = await CreateDatabaseAsync();

        try
        {
            var store = new SqliteRunQueueStore(new SqlitePathResolver(databasePath));

            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                store.SeedRecoveryProbeAsync(
                    new RunQueueRecoveryProbeRequest(RequestId: null, Status: "Pending"),
                    CancellationToken.None));

            var seeded = await store.SeedRecoveryProbeAsync(
                new RunQueueRecoveryProbeRequest(
                    RequestId: "",
                    Status: "CancelRequested",
                    Mode: "",
                    RunTrigger: "",
                    RunId: "run-recovery",
                    WorkerName: null,
                    StartedMinutesAgo: 0),
                CancellationToken.None);

            Assert.StartsWith("recovery-probe-", seeded.RequestId, StringComparison.Ordinal);
            Assert.Equal("BulkSync", seeded.Mode);
            Assert.Equal("AutomationRecoveryProbe", seeded.RunTrigger);
            Assert.Equal("CancelRequested", seeded.Status);
            Assert.Equal("run-recovery", seeded.RunId);
            Assert.NotNull(seeded.StartedAt);

            var persisted = await store.GetAsync(seeded.RequestId, CancellationToken.None);
            Assert.Equal("CancelRequested", persisted!.Status);
            Assert.Equal("run-recovery", persisted.RunId);
        }
        finally
        {
            File.Delete(databasePath);
        }
    }

    [Fact]
    public async Task GetAsync_ReturnsTerminalRequestById()
    {
        var databasePath = await CreateDatabaseAsync();

        try
        {
            await ExecuteAsync(
                databasePath,
                """
                INSERT INTO run_queue (
                  request_id, mode, dry_run, run_trigger, requested_by, status, requested_at, started_at, completed_at, run_id, worker_name, error_message
                )
                VALUES
                  ('req-completed', 'BulkSync', 0, 'Automation', 'automation', 'Completed', '2026-04-06T12:00:00Z', '2026-04-06T12:01:00Z', '2026-04-06T12:02:00Z', 'bulk-1', 'SyncFactors.Worker', NULL),
                  ('req-failed', 'BulkSync', 0, 'Automation', 'automation', 'Failed', '2026-04-06T13:00:00Z', '2026-04-06T13:01:00Z', '2026-04-06T13:02:00Z', NULL, 'SyncFactors.Worker', 'boom');
                """);

            var store = new SqliteRunQueueStore(new SqlitePathResolver(databasePath));

            var completed = await store.GetAsync("req-completed", CancellationToken.None);
            var failed = await store.GetAsync("req-failed", CancellationToken.None);
            var missing = await store.GetAsync("missing", CancellationToken.None);

            Assert.NotNull(completed);
            Assert.Equal("Completed", completed!.Status);
            Assert.Equal("bulk-1", completed.RunId);
            Assert.False(completed.DryRun);
            Assert.Equal("Automation", completed.RunTrigger);

            Assert.NotNull(failed);
            Assert.Equal("Failed", failed!.Status);
            Assert.Equal("boom", failed.ErrorMessage);
            Assert.Null(missing);
        }
        finally
        {
            File.Delete(databasePath);
        }
    }

    [Fact]
    public async Task RecoverOrphanedActiveRunsAsync_TransitionsActiveStatusesToTerminalStatuses()
    {
        var databasePath = await CreateDatabaseAsync();

        try
        {
            await ExecuteAsync(
                databasePath,
                """
                INSERT INTO run_queue (
                  request_id, mode, dry_run, run_trigger, requested_by, status, requested_at, started_at, completed_at, run_id, worker_name, error_message
                )
                VALUES
                  ('req-1', 'BulkSync', 1, 'AdHoc', 'test', 'InProgress', '2026-04-06T12:00:00Z', '2026-04-06T12:01:00Z', NULL, NULL, 'SyncFactors.Worker', NULL),
                  ('req-2', 'BulkSync', 1, 'AdHoc', 'test', 'CancelRequested', '2026-04-06T12:00:00Z', '2026-04-06T12:01:00Z', NULL, NULL, 'SyncFactors.Worker', 'Cancellation requested.'),
                  ('req-3', 'BulkSync', 1, 'AdHoc', 'test', 'Pending', '2026-04-06T12:00:00Z', NULL, NULL, NULL, NULL, NULL);
                """);

            var store = new SqliteRunQueueStore(new SqlitePathResolver(databasePath));
            var recovered = await store.RecoverOrphanedActiveRunsAsync("Recovered on startup.", CancellationToken.None);

            Assert.Equal(2, recovered);

            await using var verifyConnection = new SqliteConnection($"Data Source={databasePath};Mode=ReadOnly");
            await verifyConnection.OpenAsync();
            await using var verifyCommand = verifyConnection.CreateCommand();
            verifyCommand.CommandText = "SELECT request_id, status, completed_at, error_message FROM run_queue ORDER BY request_id ASC;";
            await using var reader = await verifyCommand.ExecuteReaderAsync();

            Assert.True(await reader.ReadAsync());
            Assert.Equal("req-1", reader.GetString(0));
            Assert.Equal("Failed", reader.GetString(1));
            Assert.False(reader.IsDBNull(2));
            Assert.Equal("Recovered on startup.", reader.GetString(3));

            Assert.True(await reader.ReadAsync());
            Assert.Equal("req-2", reader.GetString(0));
            Assert.Equal("Canceled", reader.GetString(1));
            Assert.False(reader.IsDBNull(2));
            Assert.Equal("Cancellation requested.", reader.GetString(3));

            Assert.True(await reader.ReadAsync());
            Assert.Equal("req-3", reader.GetString(0));
            Assert.Equal("Pending", reader.GetString(1));
            Assert.True(reader.IsDBNull(2));
            Assert.True(reader.IsDBNull(3));
        }
        finally
        {
            File.Delete(databasePath);
        }
    }

    private static async Task<string> CreateDatabaseAsync()
    {
        var databasePath = Path.Combine(Path.GetTempPath(), $"syncfactors-run-queue-{Guid.NewGuid():N}.db");
        var initializer = new SqliteDatabaseInitializer(new SqlitePathResolver(databasePath));
        await initializer.InitializeAsync(CancellationToken.None);
        return databasePath;
    }

    private static async Task ExecuteAsync(string databasePath, string commandText)
    {
        await using var connection = new SqliteConnection($"Data Source={databasePath}");
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = commandText;
        await command.ExecuteNonQueryAsync();
    }
}

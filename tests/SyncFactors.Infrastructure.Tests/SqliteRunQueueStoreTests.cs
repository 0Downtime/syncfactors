using Microsoft.Data.Sqlite;
using SyncFactors.Contracts;
using SyncFactors.Domain;
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
    public async Task EnqueueAsync_ConcurrentRequestsAllowOnlyOnePendingRunAndUseGuidRequestIds()
    {
        var databasePath = await CreateDatabaseAsync();

        try
        {
            using var start = new ManualResetEventSlim(false);
            var stores = Enumerable.Range(0, 12)
                .Select(_ => new SqliteRunQueueStore(new SqlitePathResolver(databasePath)))
                .ToArray();

            var attempts = stores.Select(store => Task.Run(async () =>
            {
                start.Wait();
                try
                {
                    return await store.EnqueueAsync(
                        new StartRunRequest(DryRun: true, RequestedBy: "concurrent-test"),
                        CancellationToken.None);
                }
                catch (InvalidOperationException)
                {
                    return null;
                }
            })).ToArray();

            start.Set();
            var queued = (await Task.WhenAll(attempts)).Where(request => request is not null).ToArray();

            var request = Assert.Single(queued);
            Assert.True(Guid.TryParse(request!.RequestId["runreq-".Length..], out _));
            Assert.True(await stores[0].HasPendingOrActiveRunAsync(CancellationToken.None));
        }
        finally
        {
            File.Delete(databasePath);
        }
    }

    [Fact]
    public async Task ClaimNextPendingAsync_ConcurrentWorkersAtomicallyClaimOnlyOneRequest()
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
                VALUES ('req-pending', 'BulkSync', 1, 'AdHoc', 'test', 'Pending', '2026-04-06T12:00:00Z', NULL, NULL, NULL, NULL, NULL);
                """);

            using var start = new ManualResetEventSlim(false);
            var stores = Enumerable.Range(0, 12)
                .Select(_ => new SqliteRunQueueStore(new SqlitePathResolver(databasePath)))
                .ToArray();
            var attempts = stores.Select((store, index) => Task.Run(async () =>
            {
                start.Wait();
                return await store.ClaimNextPendingAsync($"worker-{index}", CancellationToken.None);
            })).ToArray();

            start.Set();
            var claimed = (await Task.WhenAll(attempts)).Where(request => request is not null).ToArray();

            var request = Assert.Single(claimed);
            Assert.Equal("req-pending", request!.RequestId);
            Assert.Equal("InProgress", request.Status);
        }
        finally
        {
            File.Delete(databasePath);
        }
    }

    [Fact]
    public async Task ClaimNextPendingAsync_ClaimsPendingRequest()
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
                VALUES ('req-pending', 'BulkSync', 0, 'Scheduled', 'older', 'Pending', '2026-04-06T12:00:00Z', NULL, NULL, NULL, NULL, NULL);
                """);

            var store = new SqliteRunQueueStore(new SqlitePathResolver(databasePath));

            var claimed = await store.ClaimNextPendingAsync("worker-a", CancellationToken.None);

            Assert.NotNull(claimed);
            Assert.Equal("req-pending", claimed!.RequestId);
            Assert.Equal("InProgress", claimed.Status);
            Assert.NotNull(claimed.StartedAt);

            var persisted = await store.GetAsync("req-pending", CancellationToken.None);
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
                VALUES ('req-active', 'BulkSync', 1, 'AdHoc', 'test', 'InProgress', '2026-04-06T12:00:00Z', '2026-04-06T12:01:00Z', NULL, NULL, 'worker-a', NULL);
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
    public async Task FailAsync_DoesNotClobberATerminalRequestWhenAStaleWorkerReportsLate()
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
                VALUES ('req-completed', 'BulkSync', 0, 'AdHoc', 'test', 'Completed', '2026-04-06T12:00:00Z', '2026-04-06T12:01:00Z', '2026-04-06T12:02:00Z', 'run-1', 'worker-a', NULL);
                """);
            var store = new SqliteRunQueueStore(new SqlitePathResolver(databasePath));

            await store.FailAsync("req-completed", "stale-run", "late worker failure", CancellationToken.None);

            var persisted = await store.GetAsync("req-completed", CancellationToken.None);
            Assert.Equal("Completed", persisted!.Status);
            Assert.Equal("run-1", persisted.RunId);
            Assert.Null(persisted.ErrorMessage);
        }
        finally
        {
            File.Delete(databasePath);
        }
    }

    [Fact]
    public async Task GetPendingOrActiveAsync_ReturnsTheOneNonTerminalRequest()
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
                VALUES ('req-pending', 'BulkSync', 1, 'AdHoc', 'test', 'Pending', '2026-04-06T12:00:00Z', NULL, NULL, NULL, NULL, NULL);
                """);

            var store = new SqliteRunQueueStore(new SqlitePathResolver(databasePath));

            var pending = await store.GetPendingOrActiveAsync(CancellationToken.None);
            Assert.Equal("req-pending", pending!.RequestId);
            Assert.True(await store.HasPendingOrActiveRunAsync(CancellationToken.None));

            await ExecuteAsync(databasePath, "UPDATE run_queue SET status = 'CancelRequested', started_at = '2026-04-06T12:02:00Z' WHERE request_id = 'req-pending';");
            var cancelRequested = await store.GetPendingOrActiveAsync(CancellationToken.None);
            Assert.Equal("req-pending", cancelRequested!.RequestId);

            await ExecuteAsync(databasePath, "UPDATE run_queue SET status = 'Completed', completed_at = '2026-04-06T12:05:00Z' WHERE request_id = 'req-pending';");
            Assert.Null(await store.GetPendingOrActiveAsync(CancellationToken.None));
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
    public async Task CancelPendingOrActiveAsync_ClaimedRunIsRecoveredBeforeAnotherRunCanBeEnqueued()
    {
        var databasePath = await CreateDatabaseAsync();

        try
        {
            var store = new SqliteRunQueueStore(new SqlitePathResolver(databasePath));
            var queued = await store.EnqueueAsync(new StartRunRequest(DryRun: true), CancellationToken.None);
            var claimed = await store.ClaimNextPendingAsync("worker-a", CancellationToken.None);

            Assert.Equal(queued.RequestId, claimed!.RequestId);
            Assert.True(await store.CancelPendingOrActiveAsync("operator", CancellationToken.None));
            Assert.True(await store.IsCancellationRequestedAsync(queued.RequestId, CancellationToken.None));
            await Assert.ThrowsAsync<RunQueueConflictException>(() =>
                store.EnqueueAsync(new StartRunRequest(DryRun: true), CancellationToken.None));

            Assert.Equal(1, await store.RecoverOrphanedActiveRunsAsync("Recovered on startup.", CancellationToken.None));

            var recovered = await store.GetAsync(queued.RequestId, CancellationToken.None);
            Assert.Equal("Canceled", recovered!.Status);
            Assert.NotNull(recovered.CompletedAt);

            var next = await store.EnqueueAsync(new StartRunRequest(DryRun: true), CancellationToken.None);
            Assert.Equal("Pending", next.Status);
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
                VALUES ('req-cancel', 'BulkSync', 1, 'AdHoc', 'test', 'CancelRequested', '2026-04-06T12:00:00Z', NULL, NULL, NULL, NULL, NULL);
                """);

            var store = new SqliteRunQueueStore(new SqlitePathResolver(databasePath));

            Assert.True(await store.IsCancellationRequestedAsync("req-cancel", CancellationToken.None));
            await ExecuteAsync(databasePath, "UPDATE run_queue SET status = 'InProgress' WHERE request_id = 'req-cancel';");
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
                VALUES ('req-complete', 'BulkSync', 1, 'AdHoc', 'test', 'InProgress', '2026-04-06T12:00:00Z', NULL, NULL, NULL, NULL, NULL);
                """);

            var store = new SqliteRunQueueStore(new SqlitePathResolver(databasePath));

            await store.CompleteAsync("req-complete", "run-1", CancellationToken.None);
            await ExecuteAsync(databasePath, "INSERT INTO run_queue (request_id, mode, dry_run, run_trigger, requested_by, status, requested_at, started_at, completed_at, run_id, worker_name, error_message) VALUES ('req-cancel', 'BulkSync', 1, 'AdHoc', 'test', 'CancelRequested', '2026-04-06T12:01:00Z', NULL, NULL, NULL, NULL, NULL);");
            await store.CancelAsync("req-cancel", null, "Stopped.", CancellationToken.None);
            await ExecuteAsync(databasePath, "INSERT INTO run_queue (request_id, mode, dry_run, run_trigger, requested_by, status, requested_at, started_at, completed_at, run_id, worker_name, error_message) VALUES ('req-fail', 'BulkSync', 1, 'AdHoc', 'test', 'InProgress', '2026-04-06T12:02:00Z', NULL, NULL, NULL, NULL, NULL);");
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
    public async Task SeedRecoveryProbeAsync_ConflictingProbePreservesExistingNonTerminalRequest()
    {
        var databasePath = await CreateDatabaseAsync();

        try
        {
            var store = new SqliteRunQueueStore(new SqlitePathResolver(databasePath));
            var active = await store.EnqueueAsync(new StartRunRequest(DryRun: true, RequestedBy: "operator"), CancellationToken.None);

            await Assert.ThrowsAsync<RunQueueConflictException>(() =>
                store.SeedRecoveryProbeAsync(
                    new RunQueueRecoveryProbeRequest(RequestId: "recovery-probe-conflict", Status: "InProgress"),
                    CancellationToken.None));

            var persisted = await store.GetAsync(active.RequestId, CancellationToken.None);
            Assert.NotNull(persisted);
            Assert.Equal("Pending", persisted!.Status);
            Assert.Null(await store.GetAsync("recovery-probe-conflict", CancellationToken.None));
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
    public async Task RecoverOrphanedActiveRunsAsync_TransitionsTheActiveStatusToATerminalStatus()
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
                VALUES ('req-1', 'BulkSync', 1, 'AdHoc', 'test', 'InProgress', '2026-04-06T12:00:00Z', '2026-04-06T12:01:00Z', NULL, NULL, 'SyncFactors.Worker', NULL);
                """);

            var store = new SqliteRunQueueStore(new SqlitePathResolver(databasePath));
            var recovered = await store.RecoverOrphanedActiveRunsAsync("Recovered on startup.", CancellationToken.None);

            Assert.Equal(1, recovered);

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

            Assert.False(await reader.ReadAsync());
        }
        finally
        {
            File.Delete(databasePath);
        }
    }

    [Fact]
    public async Task InitializeAsync_RepairsLegacyDuplicateNonTerminalRequestsAndAddsExclusiveIndex()
    {
        var databasePath = await CreateDatabaseAsync();

        try
        {
            await ExecuteAsync(
                databasePath,
                """
                DROP INDEX idx_run_queue_one_non_terminal;
                DELETE FROM schema_versions WHERE version = 15;
                INSERT INTO run_queue (request_id, mode, dry_run, run_trigger, requested_by, status, requested_at, started_at, completed_at, run_id, worker_name, error_message)
                VALUES
                  ('req-active', 'BulkSync', 1, 'AdHoc', 'test', 'InProgress', '2026-04-06T12:00:00Z', '2026-04-06T12:01:00Z', NULL, NULL, 'worker-a', NULL),
                  ('req-pending', 'BulkSync', 1, 'AdHoc', 'test', 'Pending', '2026-04-06T12:02:00Z', NULL, NULL, NULL, NULL, NULL);
                """);

            await new SqliteDatabaseInitializer(new SqlitePathResolver(databasePath)).InitializeAsync(CancellationToken.None);

            await using var connection = new SqliteConnection($"Data Source={databasePath};Mode=ReadOnly");
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText =
                """
                SELECT COUNT(*) FROM run_queue WHERE status IN ('Pending', 'InProgress', 'CancelRequested');
                SELECT status FROM run_queue WHERE request_id = 'req-pending';
                SELECT COUNT(*) FROM sqlite_master WHERE type = 'index' AND name = 'idx_run_queue_one_non_terminal';
                """;

            Assert.Equal(1L, (long)(await command.ExecuteScalarAsync())!);
            await using var reader = await command.ExecuteReaderAsync();
            Assert.True(await reader.NextResultAsync());
            Assert.True(await reader.ReadAsync());
            Assert.Equal("Canceled", reader.GetString(0));
            Assert.True(await reader.NextResultAsync());
            Assert.True(await reader.ReadAsync());
            Assert.Equal(1L, reader.GetInt64(0));
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

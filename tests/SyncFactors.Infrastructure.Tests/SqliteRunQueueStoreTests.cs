using Microsoft.Data.Sqlite;
using SyncFactors.Infrastructure;

namespace SyncFactors.Infrastructure.Tests;

public sealed class SqliteRunQueueStoreTests
{
    [Fact]
    public async Task GetAsync_ReturnsTerminalRequestById()
    {
        var databasePath = Path.Combine(Path.GetTempPath(), $"syncfactors-run-queue-get-{Guid.NewGuid():N}.db");

        try
        {
            var initializer = new SqliteDatabaseInitializer(new SqlitePathResolver(databasePath));
            await initializer.InitializeAsync(CancellationToken.None);

            await using (var connection = new SqliteConnection($"Data Source={databasePath}"))
            {
                await connection.OpenAsync();
                await using var command = connection.CreateCommand();
                command.CommandText =
                    """
                    INSERT INTO run_queue (
                      request_id, mode, dry_run, run_trigger, requested_by, status, requested_at, started_at, completed_at, run_id, worker_name, error_message
                    )
                    VALUES
                      ('req-completed', 'BulkSync', 0, 'Automation', 'automation', 'Completed', '2026-04-06T12:00:00Z', '2026-04-06T12:01:00Z', '2026-04-06T12:02:00Z', 'bulk-1', 'SyncFactors.Worker', NULL),
                      ('req-failed', 'BulkSync', 0, 'Automation', 'automation', 'Failed', '2026-04-06T13:00:00Z', '2026-04-06T13:01:00Z', '2026-04-06T13:02:00Z', NULL, 'SyncFactors.Worker', 'boom');
                    """;
                await command.ExecuteNonQueryAsync();
            }

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
        var databasePath = Path.Combine(Path.GetTempPath(), $"syncfactors-run-queue-{Guid.NewGuid():N}.db");

        try
        {
            var initializer = new SqliteDatabaseInitializer(new SqlitePathResolver(databasePath));
            await initializer.InitializeAsync(CancellationToken.None);

            await using (var connection = new SqliteConnection($"Data Source={databasePath}"))
            {
                await connection.OpenAsync();
                await using var command = connection.CreateCommand();
                command.CommandText =
                    """
                    INSERT INTO run_queue (
                      request_id, mode, dry_run, run_trigger, requested_by, status, requested_at, started_at, completed_at, run_id, worker_name, error_message
                    )
                    VALUES
                      ('req-1', 'BulkSync', 1, 'AdHoc', 'test', 'InProgress', '2026-04-06T12:00:00Z', '2026-04-06T12:01:00Z', NULL, NULL, 'SyncFactors.Worker', NULL),
                      ('req-2', 'BulkSync', 1, 'AdHoc', 'test', 'CancelRequested', '2026-04-06T12:00:00Z', '2026-04-06T12:01:00Z', NULL, NULL, 'SyncFactors.Worker', 'Cancellation requested.'),
                      ('req-3', 'BulkSync', 1, 'AdHoc', 'test', 'Pending', '2026-04-06T12:00:00Z', NULL, NULL, NULL, NULL, NULL);
                    """;
                await command.ExecuteNonQueryAsync();
            }

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
}

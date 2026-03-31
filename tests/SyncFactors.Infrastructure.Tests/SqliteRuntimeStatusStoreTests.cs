using Microsoft.Data.Sqlite;
using SyncFactors.Contracts;
using SyncFactors.Infrastructure;

namespace SyncFactors.Infrastructure.Tests;

public sealed class SqliteRuntimeStatusStoreTests
{
    [Fact]
    public async Task SaveAsync_UpsertsCurrentSchemaAndRoundTripsSnapshot()
    {
        var databasePath = Path.Combine(Path.GetTempPath(), $"syncfactors-runtime-status-{Guid.NewGuid():N}.db");

        try
        {
            await CreateCurrentRuntimeStatusSchemaAsync(databasePath);

            var store = new SqliteRuntimeStatusStore(new SqlitePathResolver(databasePath));
            var expected = new RuntimeStatus(
                Status: "InProgress",
                Stage: "ApplyPreview",
                RunId: "apply-worker-123",
                Mode: "ApplyPreview",
                DryRun: false,
                ProcessedWorkers: 1,
                TotalWorkers: 2,
                CurrentWorkerId: "worker-123",
                LastAction: "Updating worker-123",
                StartedAt: DateTimeOffset.Parse("2026-03-30T12:00:00Z"),
                LastUpdatedAt: DateTimeOffset.Parse("2026-03-30T12:01:00Z"),
                CompletedAt: null,
                ErrorMessage: null);

            await store.SaveAsync(expected, CancellationToken.None);

            await using var connection = new SqliteConnection($"Data Source={databasePath};Mode=ReadOnly");
            await connection.OpenAsync();

            await using (var command = connection.CreateCommand())
            {
                command.CommandText = "SELECT state_path, snapshot_json FROM runtime_status;";
                await using var reader = await command.ExecuteReaderAsync();
                Assert.True(await reader.ReadAsync());
                Assert.Equal("current", reader.GetString(0));

                var snapshotJson = reader.GetString(1);
                Assert.Contains("\"Mode\":\"ApplyPreview\"", snapshotJson, StringComparison.Ordinal);
                Assert.Contains("\"DryRun\":false", snapshotJson, StringComparison.Ordinal);
            }

            var actual = await store.GetCurrentAsync(CancellationToken.None);

            Assert.Equal(expected, actual);
        }
        finally
        {
            File.Delete(databasePath);
        }
    }

    [Fact]
    public async Task TryStartAsync_RejectsWhenRuntimeStatusIsAlreadyInProgress()
    {
        var databasePath = Path.Combine(Path.GetTempPath(), $"syncfactors-runtime-start-{Guid.NewGuid():N}.db");

        try
        {
            await CreateCurrentRuntimeStatusSchemaAsync(databasePath);

            var store = new SqliteRuntimeStatusStore(new SqlitePathResolver(databasePath));
            var firstStatus = new RuntimeStatus(
                Status: "InProgress",
                Stage: "Planning",
                RunId: "run-1",
                Mode: "FullSyncLive",
                DryRun: false,
                ProcessedWorkers: 0,
                TotalWorkers: 0,
                CurrentWorkerId: null,
                LastAction: "Starting",
                StartedAt: DateTimeOffset.Parse("2026-03-30T12:00:00Z"),
                LastUpdatedAt: DateTimeOffset.Parse("2026-03-30T12:00:00Z"),
                CompletedAt: null,
                ErrorMessage: null);
            var secondStatus = firstStatus with { RunId = "run-2" };

            Assert.True(await store.TryStartAsync(firstStatus, CancellationToken.None));
            Assert.False(await store.TryStartAsync(secondStatus, CancellationToken.None));

            var actual = await store.GetCurrentAsync(CancellationToken.None);
            Assert.Equal("run-1", actual!.RunId);
        }
        finally
        {
            File.Delete(databasePath);
        }
    }

    [Fact]
    public async Task InitializeAsync_UpgradesLegacyRuntimeStatusTableToSnapshotSchema()
    {
        var databasePath = Path.Combine(Path.GetTempPath(), $"syncfactors-runtime-migration-{Guid.NewGuid():N}.db");

        try
        {
            await CreateLegacyRuntimeStatusSchemaAsync(databasePath);

            var initializer = new SqliteDatabaseInitializer(new SqlitePathResolver(databasePath));
            await initializer.InitializeAsync(CancellationToken.None);

            await using var connection = new SqliteConnection($"Data Source={databasePath};Mode=ReadOnly");
            await connection.OpenAsync();

            await using (var schemaCommand = connection.CreateCommand())
            {
                schemaCommand.CommandText = "PRAGMA table_info(runtime_status);";
                await using var reader = await schemaCommand.ExecuteReaderAsync();

                var columns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                while (await reader.ReadAsync())
                {
                    columns.Add(reader.GetString(1));
                }

                Assert.Contains("state_path", columns);
                Assert.Contains("snapshot_json", columns);
            }

            var store = new SqliteRuntimeStatusStore(new SqlitePathResolver(databasePath));
            var status = await store.GetCurrentAsync(CancellationToken.None);

            Assert.NotNull(status);
            Assert.Equal("legacy-run", status!.RunId);
            Assert.Equal("InProgress", status.Status);
            Assert.Equal("Planning", status.Stage);
        }
        finally
        {
            File.Delete(databasePath);
        }
    }

    private static async Task CreateCurrentRuntimeStatusSchemaAsync(string databasePath)
    {
        await using var connection = new SqliteConnection($"Data Source={databasePath};Mode=ReadWriteCreate");
        await connection.OpenAsync();

        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            CREATE TABLE runtime_status (
              state_path TEXT PRIMARY KEY,
              run_id TEXT NULL,
              status TEXT NULL,
              stage TEXT NULL,
              started_at TEXT NULL,
              last_updated_at TEXT NULL,
              completed_at TEXT NULL,
              current_worker_id TEXT NULL,
              last_action TEXT NULL,
              processed_workers INTEGER NOT NULL DEFAULT 0,
              total_workers INTEGER NOT NULL DEFAULT 0,
              error_message TEXT NULL,
              snapshot_json TEXT NOT NULL
            );
            """;
        await command.ExecuteNonQueryAsync();
    }

    private static async Task CreateLegacyRuntimeStatusSchemaAsync(string databasePath)
    {
        await using var connection = new SqliteConnection($"Data Source={databasePath};Mode=ReadWriteCreate");
        await connection.OpenAsync();

        await using (var schemaCommand = connection.CreateCommand())
        {
            schemaCommand.CommandText =
                """
                CREATE TABLE schema_versions (
                  version INTEGER NOT NULL PRIMARY KEY,
                  applied_at TEXT NOT NULL
                );

                INSERT INTO schema_versions (version, applied_at)
                VALUES (1, '2026-03-30T00:00:00Z'),
                       (2, '2026-03-30T00:01:00Z');

                CREATE TABLE runtime_status (
                  run_id TEXT NULL,
                  status TEXT NULL,
                  stage TEXT NULL,
                  started_at TEXT NULL,
                  last_updated_at TEXT NULL,
                  completed_at TEXT NULL,
                  current_worker_id TEXT NULL,
                  last_action TEXT NULL,
                  processed_workers INTEGER NOT NULL DEFAULT 0,
                  total_workers INTEGER NOT NULL DEFAULT 0,
                  error_message TEXT NULL
                );
                """;
            await schemaCommand.ExecuteNonQueryAsync();
        }

        await using var insertCommand = connection.CreateCommand();
        insertCommand.CommandText =
            """
            INSERT INTO runtime_status (
              run_id,
              status,
              stage,
              started_at,
              last_updated_at,
              completed_at,
              current_worker_id,
              last_action,
              processed_workers,
              total_workers,
              error_message
            )
            VALUES (
              'legacy-run',
              'InProgress',
              'Planning',
              '2026-03-30T00:00:00Z',
              '2026-03-30T00:01:00Z',
              NULL,
              'worker-legacy',
              'Bootstrapping',
              1,
              5,
              NULL
            );
            """;
        await insertCommand.ExecuteNonQueryAsync();
    }
}

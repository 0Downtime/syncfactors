using Microsoft.Data.Sqlite;
using SyncFactors.Infrastructure;

namespace SyncFactors.Infrastructure.Tests;

public sealed class SqliteDatabaseMigrationTests
{
    [Fact]
    public async Task InitializeAsync_RejectsDatabaseFromNewerSchemaWithoutDowngradingIt()
    {
        var databasePath = Path.Combine(Path.GetTempPath(), $"syncfactors-future-schema-{Guid.NewGuid():N}.db");

        try
        {
            await CreateSchemaVersionDatabaseAsync(databasePath, version: 19);

            var exception = await Assert.ThrowsAsync<InvalidOperationException>(
                () => new SqliteDatabaseInitializer(new SqlitePathResolver(databasePath)).InitializeAsync(CancellationToken.None));

            Assert.Contains("newer", exception.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Equal(new[] { 19 }, await GetAppliedVersionsAsync(databasePath));
            Assert.False(await TableExistsAsync(databasePath, "runs"));
        }
        finally
        {
            DeleteDatabaseFiles(databasePath);
        }
    }

    [Fact]
    public async Task InitializeAsync_MigratesOldestSupportedSchemaThroughEveryVersion()
    {
        var databasePath = Path.Combine(Path.GetTempPath(), $"syncfactors-oldest-schema-{Guid.NewGuid():N}.db");

        try
        {
            await CreateOldestSupportedSchemaAsync(databasePath);

            await new SqliteDatabaseInitializer(new SqlitePathResolver(databasePath)).InitializeAsync(CancellationToken.None);

            Assert.Equal(Enumerable.Range(1, 18), await GetAppliedVersionsAsync(databasePath));
            foreach (var table in new[] { "run_queue", "sync_schedule", "delta_sync_state", "local_users", "graveyard_retention", "dashboard_settings", "oidc_accounts", "maintenance_state", "directory_deletion_quarantine" })
            {
                Assert.True(await TableExistsAsync(databasePath, table));
            }

            Assert.Contains("state_path", await GetTableColumnsAsync(databasePath, "runtime_status"));
            Assert.Contains("snapshot_json", await GetTableColumnsAsync(databasePath, "runtime_status"));
            Assert.Contains("run_trigger", await GetTableColumnsAsync(databasePath, "runs"));
            Assert.Contains("requested_by", await GetTableColumnsAsync(databasePath, "runs"));
            Assert.Contains("failed_login_count", await GetTableColumnsAsync(databasePath, "local_users"));
            Assert.Contains("lockout_end_at", await GetTableColumnsAsync(databasePath, "local_users"));
            Assert.Contains("is_on_hold", await GetTableColumnsAsync(databasePath, "graveyard_retention"));
            Assert.Contains("version", await GetTableColumnsAsync(databasePath, "graveyard_retention"));
            Assert.Contains("deletion_claim_id", await GetTableColumnsAsync(databasePath, "graveyard_retention"));
            Assert.Contains("deletion_claim_version", await GetTableColumnsAsync(databasePath, "graveyard_retention"));
            Assert.Contains("deletion_lease_expires_at_utc", await GetTableColumnsAsync(databasePath, "graveyard_retention"));
            Assert.Contains("health_probe_interval_seconds", await GetTableColumnsAsync(databasePath, "dashboard_settings"));
            Assert.Contains("source_kind", await GetTableColumnsAsync(databasePath, "directory_deletion_quarantine"));
            Assert.Contains("source_id", await GetTableColumnsAsync(databasePath, "directory_deletion_quarantine"));
        }
        finally
        {
            DeleteDatabaseFiles(databasePath);
        }
    }

    [Fact]
    public async Task InitializeAsync_RollsBackEarlierMigrationStepsWhenALaterMigrationFails()
    {
        var databasePath = Path.Combine(Path.GetTempPath(), $"syncfactors-rollback-schema-{Guid.NewGuid():N}.db");

        try
        {
            await CreateOldestSupportedSchemaAsync(databasePath);
            await ExecuteAsync(databasePath, "CREATE TABLE runtime_status_legacy (id INTEGER PRIMARY KEY);");

            await Assert.ThrowsAsync<SqliteException>(
                () => new SqliteDatabaseInitializer(new SqlitePathResolver(databasePath)).InitializeAsync(CancellationToken.None));

            Assert.Equal(new[] { 1 }, await GetAppliedVersionsAsync(databasePath));
            Assert.False(await TableExistsAsync(databasePath, "worker_heartbeat"));
            Assert.True(await TableExistsAsync(databasePath, "runtime_status"));
        }
        finally
        {
            DeleteDatabaseFiles(databasePath);
        }
    }

    [Fact]
    public async Task InitializeAsync_MigratesOldestSupportedSchemaAfterEncryptingIt()
    {
        const string passwordEnvironmentVariable = "SYNCFACTORS_SQLITE_PASSWORD";
        const string configurationPasswordEnvironmentVariable = "SyncFactors__SqlitePassword";
        const string password = "migration-chain-test-password";
        var originalPassword = Environment.GetEnvironmentVariable(passwordEnvironmentVariable);
        var originalConfigurationPassword = Environment.GetEnvironmentVariable(configurationPasswordEnvironmentVariable);
        var databasePath = Path.Combine(Path.GetTempPath(), $"syncfactors-encrypted-oldest-schema-{Guid.NewGuid():N}.db");

        try
        {
            await CreateOldestSupportedSchemaAsync(databasePath);
            Environment.SetEnvironmentVariable(passwordEnvironmentVariable, password);
            Environment.SetEnvironmentVariable(configurationPasswordEnvironmentVariable, null);

            await new SqliteDatabaseInitializer(new SqlitePathResolver(databasePath)).InitializeAsync(CancellationToken.None);

            Assert.False(HasPlaintextSqliteHeader(databasePath));
            await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = databasePath,
                Mode = SqliteOpenMode.ReadOnly,
                Password = password,
                Pooling = false,
            }.ToString());
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT COUNT(*) FROM schema_versions;";
            Assert.Equal(18L, (long)(await command.ExecuteScalarAsync() ?? 0L));
        }
        finally
        {
            Environment.SetEnvironmentVariable(passwordEnvironmentVariable, originalPassword);
            Environment.SetEnvironmentVariable(configurationPasswordEnvironmentVariable, originalConfigurationPassword);
            DeleteDatabaseFiles(databasePath);
        }
    }

    private static async Task CreateSchemaVersionDatabaseAsync(string databasePath, int version)
    {
        await using var connection = new SqliteConnection($"Data Source={databasePath};Mode=ReadWriteCreate");
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            CREATE TABLE schema_versions (
              version INTEGER NOT NULL PRIMARY KEY,
              applied_at TEXT NOT NULL
            );

            INSERT INTO schema_versions (version, applied_at)
            VALUES ($version, $appliedAt);
            """;
        command.Parameters.AddWithValue("$version", version);
        command.Parameters.AddWithValue("$appliedAt", DateTimeOffset.UtcNow.ToString("O"));
        await command.ExecuteNonQueryAsync();
    }

    private static async Task CreateOldestSupportedSchemaAsync(string databasePath)
    {
        await using var connection = new SqliteConnection($"Data Source={databasePath};Mode=ReadWriteCreate");
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            CREATE TABLE schema_versions (version INTEGER NOT NULL PRIMARY KEY, applied_at TEXT NOT NULL);
            INSERT INTO schema_versions (version, applied_at) VALUES (1, '2026-01-01T00:00:00Z');
            CREATE TABLE runs (
              run_id TEXT NOT NULL PRIMARY KEY, path TEXT NULL, artifact_type TEXT NULL, config_path TEXT NULL,
              mapping_config_path TEXT NULL, mode TEXT NULL, dry_run INTEGER NOT NULL DEFAULT 0, status TEXT NULL,
              started_at TEXT NULL, completed_at TEXT NULL, duration_seconds INTEGER NULL, creates INTEGER NOT NULL DEFAULT 0,
              updates INTEGER NOT NULL DEFAULT 0, enables INTEGER NOT NULL DEFAULT 0, disables INTEGER NOT NULL DEFAULT 0,
              graveyard_moves INTEGER NOT NULL DEFAULT 0, deletions INTEGER NOT NULL DEFAULT 0, quarantined INTEGER NOT NULL DEFAULT 0,
              conflicts INTEGER NOT NULL DEFAULT 0, guardrail_failures INTEGER NOT NULL DEFAULT 0, manual_review INTEGER NOT NULL DEFAULT 0,
              unchanged INTEGER NOT NULL DEFAULT 0, report_json TEXT NULL
            );
            CREATE TABLE run_entries (
              entry_id TEXT NOT NULL PRIMARY KEY, run_id TEXT NOT NULL, bucket TEXT NULL, bucket_index INTEGER NOT NULL DEFAULT 0,
              worker_id TEXT NULL, sam_account_name TEXT NULL, reason TEXT NULL, review_category TEXT NULL,
              review_case_type TEXT NULL, started_at TEXT NULL, item_json TEXT NULL, FOREIGN KEY (run_id) REFERENCES runs (run_id)
            );
            CREATE INDEX idx_run_entries_run_id_bucket_worker ON run_entries (run_id, bucket, worker_id);
            CREATE INDEX idx_run_entries_run_id_entry_id ON run_entries (run_id, entry_id);
            CREATE TABLE runtime_status (
              run_id TEXT NULL, status TEXT NULL, stage TEXT NULL, started_at TEXT NULL, last_updated_at TEXT NULL,
              completed_at TEXT NULL, current_worker_id TEXT NULL, last_action TEXT NULL, processed_workers INTEGER NOT NULL DEFAULT 0,
              total_workers INTEGER NOT NULL DEFAULT 0, error_message TEXT NULL
            );
            CREATE INDEX idx_runtime_status_last_updated ON runtime_status (last_updated_at, started_at, completed_at);
            """;
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<int[]> GetAppliedVersionsAsync(string databasePath)
    {
        await using var connection = new SqliteConnection($"Data Source={databasePath};Mode=ReadOnly");
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT version FROM schema_versions ORDER BY version;";
        await using var reader = await command.ExecuteReaderAsync();

        var versions = new List<int>();
        while (await reader.ReadAsync())
        {
            versions.Add(reader.GetInt32(0));
        }

        return versions.ToArray();
    }

    private static async Task<bool> TableExistsAsync(string databasePath, string tableName)
    {
        await using var connection = new SqliteConnection($"Data Source={databasePath};Mode=ReadOnly");
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT 1 FROM sqlite_master WHERE type = 'table' AND name = $tableName;";
        command.Parameters.AddWithValue("$tableName", tableName);
        return await command.ExecuteScalarAsync() is not null;
    }

    private static async Task<HashSet<string>> GetTableColumnsAsync(string databasePath, string tableName)
    {
        await using var connection = new SqliteConnection($"Data Source={databasePath};Mode=ReadOnly");
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = $"PRAGMA table_info({tableName});";
        await using var reader = await command.ExecuteReaderAsync();

        var columns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        while (await reader.ReadAsync())
        {
            columns.Add(reader.GetString(1));
        }

        return columns;
    }

    private static async Task ExecuteAsync(string databasePath, string commandText)
    {
        await using var connection = new SqliteConnection($"Data Source={databasePath};Mode=ReadWrite");
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = commandText;
        await command.ExecuteNonQueryAsync();
    }

    private static void DeleteDatabaseFiles(string databasePath)
    {
        foreach (var path in new[] { databasePath, $"{databasePath}-wal", $"{databasePath}-shm" })
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
    }

    private static bool HasPlaintextSqliteHeader(string databasePath)
    {
        Span<byte> header = stackalloc byte[16];
        using var file = File.OpenRead(databasePath);
        return file.Read(header) == header.Length && header.SequenceEqual("SQLite format 3\0"u8);
    }
}

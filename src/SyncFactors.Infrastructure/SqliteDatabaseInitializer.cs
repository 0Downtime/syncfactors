using Microsoft.Data.Sqlite;
using System.Data.Common;

namespace SyncFactors.Infrastructure;

public sealed class SqliteDatabaseInitializer(SqlitePathResolver pathResolver)
{
    private const int CurrentSchemaVersion = 7;

    public async Task InitializeAsync(CancellationToken cancellationToken)
    {
        var databasePath = pathResolver.ResolveConfiguredPath();
        if (string.IsNullOrWhiteSpace(databasePath))
        {
            return;
        }

        var directory = Path.GetDirectoryName(databasePath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        await using var connection = OpenConnection(databasePath);
        await connection.OpenAsync(cancellationToken);

        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        await using (var command = connection.CreateCommand())
        {
            command.Transaction = (SqliteTransaction)transaction;
            command.CommandText =
                """
                CREATE TABLE IF NOT EXISTS schema_versions (
                  version INTEGER NOT NULL PRIMARY KEY,
                  applied_at TEXT NOT NULL
                );
                """;
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        var appliedVersions = await GetAppliedVersionsAsync(connection, transaction, cancellationToken);
        if (!appliedVersions.Contains(1))
        {
            await ApplyVersion1Async(connection, transaction, cancellationToken);
            await InsertVersionAsync(connection, transaction, 1, cancellationToken);
        }

        if (!appliedVersions.Contains(2))
        {
            await ApplyVersion2Async(connection, transaction, cancellationToken);
            await InsertVersionAsync(connection, transaction, 2, cancellationToken);
        }

        if (!appliedVersions.Contains(3))
        {
            await ApplyVersion3Async(connection, transaction, cancellationToken);
            await InsertVersionAsync(connection, transaction, 3, cancellationToken);
        }

        if (!appliedVersions.Contains(4))
        {
            await ApplyVersion4Async(connection, transaction, cancellationToken);
            await InsertVersionAsync(connection, transaction, 4, cancellationToken);
        }

        if (!appliedVersions.Contains(5))
        {
            await ApplyVersion5Async(connection, transaction, cancellationToken);
            await InsertVersionAsync(connection, transaction, 5, cancellationToken);
        }

        if (!appliedVersions.Contains(6))
        {
            await ApplyVersion6Async(connection, transaction, cancellationToken);
            await InsertVersionAsync(connection, transaction, 6, cancellationToken);
        }

        if (!appliedVersions.Contains(7))
        {
            await ApplyVersion7Async(connection, transaction, cancellationToken);
            await InsertVersionAsync(connection, transaction, 7, cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
    }

    private static async Task ApplyVersion1Async(
        SqliteConnection connection,
        DbTransaction transaction,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = (SqliteTransaction)transaction;
        command.CommandText =
            """
            CREATE TABLE IF NOT EXISTS runs (
              run_id TEXT NOT NULL PRIMARY KEY,
              path TEXT NULL,
              artifact_type TEXT NULL,
              config_path TEXT NULL,
              mapping_config_path TEXT NULL,
              mode TEXT NULL,
              dry_run INTEGER NOT NULL DEFAULT 0,
              status TEXT NULL,
              started_at TEXT NULL,
              completed_at TEXT NULL,
              duration_seconds INTEGER NULL,
              creates INTEGER NOT NULL DEFAULT 0,
              updates INTEGER NOT NULL DEFAULT 0,
              enables INTEGER NOT NULL DEFAULT 0,
              disables INTEGER NOT NULL DEFAULT 0,
              graveyard_moves INTEGER NOT NULL DEFAULT 0,
              deletions INTEGER NOT NULL DEFAULT 0,
              quarantined INTEGER NOT NULL DEFAULT 0,
              conflicts INTEGER NOT NULL DEFAULT 0,
              guardrail_failures INTEGER NOT NULL DEFAULT 0,
              manual_review INTEGER NOT NULL DEFAULT 0,
              unchanged INTEGER NOT NULL DEFAULT 0,
              report_json TEXT NULL
            );

            CREATE TABLE IF NOT EXISTS run_entries (
              entry_id TEXT NOT NULL PRIMARY KEY,
              run_id TEXT NOT NULL,
              bucket TEXT NULL,
              bucket_index INTEGER NOT NULL DEFAULT 0,
              worker_id TEXT NULL,
              sam_account_name TEXT NULL,
              reason TEXT NULL,
              review_category TEXT NULL,
              review_case_type TEXT NULL,
              started_at TEXT NULL,
              item_json TEXT NULL,
              FOREIGN KEY (run_id) REFERENCES runs (run_id)
            );

            CREATE INDEX IF NOT EXISTS idx_run_entries_run_id_bucket_worker
              ON run_entries (run_id, bucket, worker_id);

            CREATE INDEX IF NOT EXISTS idx_run_entries_run_id_entry_id
              ON run_entries (run_id, entry_id);

            CREATE TABLE IF NOT EXISTS runtime_status (
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

            CREATE INDEX IF NOT EXISTS idx_runtime_status_last_updated
              ON runtime_status (last_updated_at, started_at, completed_at);
            """;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task ApplyVersion2Async(
        SqliteConnection connection,
        DbTransaction transaction,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = (SqliteTransaction)transaction;
        command.CommandText =
            """
            CREATE TABLE IF NOT EXISTS worker_heartbeat (
              service TEXT NOT NULL,
              state TEXT NOT NULL,
              activity TEXT NULL,
              started_at TEXT NOT NULL,
              last_seen_at TEXT NOT NULL
            );

            CREATE INDEX IF NOT EXISTS idx_worker_heartbeat_last_seen
              ON worker_heartbeat (last_seen_at);
            """;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task ApplyVersion3Async(
        SqliteConnection connection,
        DbTransaction transaction,
        CancellationToken cancellationToken)
    {
        var hasRuntimeStatusTable = await TableExistsAsync(connection, transaction, "runtime_status", cancellationToken);
        if (!hasRuntimeStatusTable)
        {
            await CreateRuntimeStatusTableAsync(connection, transaction, cancellationToken);
            return;
        }

        var runtimeStatusColumns = await GetTableColumnsAsync(connection, transaction, "runtime_status", cancellationToken);
        var hasStatePath = runtimeStatusColumns.Contains("state_path");
        var hasSnapshotJson = runtimeStatusColumns.Contains("snapshot_json");

        if (hasStatePath && hasSnapshotJson)
        {
            await EnsureRuntimeStatusIndexAsync(connection, transaction, cancellationToken);
            return;
        }

        await using (var renameCommand = connection.CreateCommand())
        {
            renameCommand.Transaction = (SqliteTransaction)transaction;
            renameCommand.CommandText = "ALTER TABLE runtime_status RENAME TO runtime_status_legacy;";
            await renameCommand.ExecuteNonQueryAsync(cancellationToken);
        }

        await CreateRuntimeStatusTableAsync(connection, transaction, cancellationToken);

        await using (var migrateCommand = connection.CreateCommand())
        {
            migrateCommand.Transaction = (SqliteTransaction)transaction;
            migrateCommand.CommandText =
                """
                INSERT INTO runtime_status (
                  state_path,
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
                  error_message,
                  snapshot_json
                )
                SELECT
                  'current',
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
                  error_message,
                  json_object(
                    'Status', COALESCE(status, 'Idle'),
                    'Stage', COALESCE(stage, 'NotStarted'),
                    'RunId', run_id,
                    'Mode', NULL,
                    'DryRun', json('false'),
                    'ProcessedWorkers', COALESCE(processed_workers, 0),
                    'TotalWorkers', COALESCE(total_workers, 0),
                    'CurrentWorkerId', current_worker_id,
                    'LastAction', last_action,
                    'StartedAt', started_at,
                    'LastUpdatedAt', last_updated_at,
                    'CompletedAt', completed_at,
                    'ErrorMessage', error_message
                  )
                FROM runtime_status_legacy
                ORDER BY COALESCE(last_updated_at, started_at, completed_at, '') DESC
                LIMIT 1;
                """;
            await migrateCommand.ExecuteNonQueryAsync(cancellationToken);
        }

        await using (var dropCommand = connection.CreateCommand())
        {
            dropCommand.Transaction = (SqliteTransaction)transaction;
            dropCommand.CommandText = "DROP TABLE runtime_status_legacy;";
            await dropCommand.ExecuteNonQueryAsync(cancellationToken);
        }
    }

    private static async Task ApplyVersion4Async(
        SqliteConnection connection,
        DbTransaction transaction,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = (SqliteTransaction)transaction;
        command.CommandText =
            """
            CREATE TABLE IF NOT EXISTS run_queue (
              request_id TEXT NOT NULL PRIMARY KEY,
              mode TEXT NOT NULL,
              dry_run INTEGER NOT NULL DEFAULT 1,
              status TEXT NOT NULL,
              requested_at TEXT NOT NULL,
              started_at TEXT NULL,
              completed_at TEXT NULL,
              run_id TEXT NULL,
              worker_name TEXT NULL,
              error_message TEXT NULL
            );

            CREATE INDEX IF NOT EXISTS idx_run_queue_status_requested_at
              ON run_queue (status, requested_at);
            """;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task ApplyVersion5Async(
        SqliteConnection connection,
        DbTransaction transaction,
        CancellationToken cancellationToken)
    {
        var hasRunsTable = await TableExistsAsync(connection, transaction, "runs", cancellationToken);
        if (hasRunsTable)
        {
            var runColumns = await GetTableColumnsAsync(connection, transaction, "runs", cancellationToken);
            if (!runColumns.Contains("run_trigger"))
            {
                await AddRunTriggerColumnToRunsAsync(connection, transaction, cancellationToken);
            }

            if (!runColumns.Contains("requested_by"))
            {
                await AddRequestedByColumnToRunsAsync(connection, transaction, cancellationToken);
            }
        }

        var hasRunQueueTable = await TableExistsAsync(connection, transaction, "run_queue", cancellationToken);
        if (hasRunQueueTable)
        {
            var queueColumns = await GetTableColumnsAsync(connection, transaction, "run_queue", cancellationToken);
            if (!queueColumns.Contains("run_trigger"))
            {
                await AddRunTriggerColumnToRunQueueAsync(connection, transaction, cancellationToken);
            }

            if (!queueColumns.Contains("requested_by"))
            {
                await AddRequestedByColumnToRunQueueAsync(connection, transaction, cancellationToken);
            }
        }

        await using var command = connection.CreateCommand();
        command.Transaction = (SqliteTransaction)transaction;
        command.CommandText =
            """
            CREATE TABLE IF NOT EXISTS sync_schedule (
              schedule_key TEXT NOT NULL PRIMARY KEY,
              enabled INTEGER NOT NULL DEFAULT 0,
              interval_minutes INTEGER NOT NULL DEFAULT 30,
              next_run_at TEXT NULL,
              last_scheduled_run_at TEXT NULL,
              last_enqueue_attempt_at TEXT NULL,
              last_enqueue_error TEXT NULL
            );
            """;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task ApplyVersion6Async(
        SqliteConnection connection,
        DbTransaction transaction,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = (SqliteTransaction)transaction;
        command.CommandText =
            """
            CREATE TABLE IF NOT EXISTS delta_sync_state (
              sync_key TEXT NOT NULL PRIMARY KEY,
              checkpoint_utc TEXT NOT NULL,
              updated_at TEXT NOT NULL
            );
            """;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task ApplyVersion7Async(
        SqliteConnection connection,
        DbTransaction transaction,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = (SqliteTransaction)transaction;
        command.CommandText =
            """
            CREATE TABLE IF NOT EXISTS local_users (
              user_id TEXT NOT NULL PRIMARY KEY,
              username TEXT NOT NULL,
              normalized_username TEXT NOT NULL,
              password_hash TEXT NOT NULL,
              role TEXT NOT NULL,
              is_active INTEGER NOT NULL DEFAULT 1,
              created_at TEXT NOT NULL,
              updated_at TEXT NOT NULL,
              last_login_at TEXT NULL
            );

            CREATE UNIQUE INDEX IF NOT EXISTS idx_local_users_normalized_username
              ON local_users (normalized_username);
            """;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task<HashSet<int>> GetAppliedVersionsAsync(
        SqliteConnection connection,
        DbTransaction transaction,
        CancellationToken cancellationToken)
    {
        var versions = new HashSet<int>();

        await using var command = connection.CreateCommand();
        command.Transaction = (SqliteTransaction)transaction;
        command.CommandText = "SELECT version FROM schema_versions ORDER BY version ASC;";

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            versions.Add(reader.GetInt32(0));
        }

        return versions;
    }

    private static async Task<HashSet<string>> GetTableColumnsAsync(
        SqliteConnection connection,
        DbTransaction transaction,
        string tableName,
        CancellationToken cancellationToken)
    {
        var columns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        await using var command = connection.CreateCommand();
        command.Transaction = (SqliteTransaction)transaction;
        command.CommandText = tableName switch
        {
            "runs" => "PRAGMA table_info(runs);",
            "run_queue" => "PRAGMA table_info(run_queue);",
            "runtime_status" => "PRAGMA table_info(runtime_status);",
            "schema_versions" => "PRAGMA table_info(schema_versions);",
            "sync_schedule" => "PRAGMA table_info(sync_schedule);",
            "worker_heartbeat" => "PRAGMA table_info(worker_heartbeat);",
            "local_users" => "PRAGMA table_info(local_users);",
            _ => throw new InvalidOperationException($"Unsupported table name '{tableName}' for schema inspection.")
        };

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            columns.Add(reader.GetString(1));
        }

        return columns;
    }

    private static async Task<bool> TableExistsAsync(
        SqliteConnection connection,
        DbTransaction transaction,
        string tableName,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = (SqliteTransaction)transaction;
        command.CommandText =
            """
            SELECT 1
            FROM sqlite_master
            WHERE type = 'table' AND name = $tableName
            LIMIT 1;
            """;
        command.Parameters.AddWithValue("$tableName", tableName);

        var result = await command.ExecuteScalarAsync(cancellationToken);
        return result is not null;
    }

    private static async Task CreateRuntimeStatusTableAsync(
        SqliteConnection connection,
        DbTransaction transaction,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = (SqliteTransaction)transaction;
        command.CommandText =
            """
            CREATE TABLE IF NOT EXISTS runtime_status (
              state_path TEXT NOT NULL PRIMARY KEY,
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
        await command.ExecuteNonQueryAsync(cancellationToken);

        await EnsureRuntimeStatusIndexAsync(connection, transaction, cancellationToken);
    }

    private static async Task EnsureRuntimeStatusIndexAsync(
        SqliteConnection connection,
        DbTransaction transaction,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = (SqliteTransaction)transaction;
        command.CommandText =
            """
            CREATE INDEX IF NOT EXISTS idx_runtime_status_last_updated
              ON runtime_status (last_updated_at, started_at, completed_at);
            """;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task InsertVersionAsync(
        SqliteConnection connection,
        DbTransaction transaction,
        int version,
        CancellationToken cancellationToken)
    {
        await using var versionCommand = connection.CreateCommand();
        versionCommand.Transaction = (SqliteTransaction)transaction;
        versionCommand.CommandText =
            """
            INSERT INTO schema_versions (version, applied_at)
            VALUES ($version, $appliedAt);
            """;
        versionCommand.Parameters.AddWithValue("$version", version);
        versionCommand.Parameters.AddWithValue("$appliedAt", DateTimeOffset.UtcNow.ToString("O"));
        await versionCommand.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task AddRunTriggerColumnToRunsAsync(
        SqliteConnection connection,
        DbTransaction transaction,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = (SqliteTransaction)transaction;
        command.CommandText = "ALTER TABLE runs ADD COLUMN run_trigger TEXT NOT NULL DEFAULT 'AdHoc';";
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task AddRequestedByColumnToRunsAsync(
        SqliteConnection connection,
        DbTransaction transaction,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = (SqliteTransaction)transaction;
        command.CommandText = "ALTER TABLE runs ADD COLUMN requested_by TEXT NULL;";
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task AddRunTriggerColumnToRunQueueAsync(
        SqliteConnection connection,
        DbTransaction transaction,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = (SqliteTransaction)transaction;
        command.CommandText = "ALTER TABLE run_queue ADD COLUMN run_trigger TEXT NOT NULL DEFAULT 'AdHoc';";
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task AddRequestedByColumnToRunQueueAsync(
        SqliteConnection connection,
        DbTransaction transaction,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = (SqliteTransaction)transaction;
        command.CommandText = "ALTER TABLE run_queue ADD COLUMN requested_by TEXT NULL;";
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static SqliteConnection OpenConnection(string databasePath)
    {
        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
        }.ToString();
        return new SqliteConnection(connectionString);
    }

}

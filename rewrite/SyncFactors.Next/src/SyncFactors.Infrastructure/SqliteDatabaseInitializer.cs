using Microsoft.Data.Sqlite;
using System.Data.Common;

namespace SyncFactors.Infrastructure;

public sealed class SqliteDatabaseInitializer(SqlitePathResolver pathResolver)
{
    private const int CurrentSchemaVersion = 2;

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

        await ExecuteNonQueryAsync(connection, transaction,
            """
            CREATE TABLE IF NOT EXISTS schema_versions (
              version INTEGER NOT NULL PRIMARY KEY,
              applied_at TEXT NOT NULL
            );
            """,
            cancellationToken);

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

        await transaction.CommitAsync(cancellationToken);
    }

    private static async Task ApplyVersion1Async(
        SqliteConnection connection,
        DbTransaction transaction,
        CancellationToken cancellationToken)
    {
        foreach (var statement in Version1Statements)
        {
            await ExecuteNonQueryAsync(connection, transaction, statement, cancellationToken);
        }
    }

    private static async Task ApplyVersion2Async(
        SqliteConnection connection,
        DbTransaction transaction,
        CancellationToken cancellationToken)
    {
        foreach (var statement in Version2Statements)
        {
            await ExecuteNonQueryAsync(connection, transaction, statement, cancellationToken);
        }
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

    private static async Task ExecuteNonQueryAsync(
        SqliteConnection connection,
        DbTransaction transaction,
        string sql,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = (SqliteTransaction)transaction;
        command.CommandText = sql;
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

    private static SqliteConnection OpenConnection(string databasePath)
    {
        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
        }.ToString();
        return new SqliteConnection(connectionString);
    }

    private static readonly string[] Version1Statements =
    [
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
        """,
        """
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
        """,
        """
        CREATE INDEX IF NOT EXISTS idx_run_entries_run_id_bucket_worker
          ON run_entries (run_id, bucket, worker_id);
        """,
        """
        CREATE INDEX IF NOT EXISTS idx_run_entries_run_id_entry_id
          ON run_entries (run_id, entry_id);
        """,
        """
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
        """,
        """
        CREATE INDEX IF NOT EXISTS idx_runtime_status_last_updated
          ON runtime_status (last_updated_at, started_at, completed_at);
        """,
    ];

    private static readonly string[] Version2Statements =
    [
        """
        CREATE TABLE IF NOT EXISTS worker_heartbeat (
          service TEXT NOT NULL,
          state TEXT NOT NULL,
          activity TEXT NULL,
          started_at TEXT NOT NULL,
          last_seen_at TEXT NOT NULL
        );
        """,
        """
        CREATE INDEX IF NOT EXISTS idx_worker_heartbeat_last_seen
          ON worker_heartbeat (last_seen_at);
        """
    ];
}

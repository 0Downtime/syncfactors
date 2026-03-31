using Microsoft.Data.Sqlite;
using SyncFactors.Contracts;
using SyncFactors.Domain;
using System.Text.Json;

namespace SyncFactors.Infrastructure;

public sealed class SqliteRuntimeStatusStore(SqlitePathResolver pathResolver) : IRuntimeStatusStore
{
    private const string CurrentStatePath = "current";

    public async Task<RuntimeStatus?> GetCurrentAsync(CancellationToken cancellationToken)
    {
        var databasePath = pathResolver.Resolve();
        if (string.IsNullOrWhiteSpace(databasePath))
        {
            return null;
        }

        await using var connection = OpenConnection(databasePath);
        await connection.OpenAsync(cancellationToken);

        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT
              snapshot_json,
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
            FROM runtime_status
            ORDER BY COALESCE(last_updated_at, started_at, completed_at, '') DESC
            LIMIT 1;
            """;

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        var snapshotJson = reader.GetStringOrDefault("snapshot_json");
        if (!string.IsNullOrWhiteSpace(snapshotJson))
        {
            try
            {
                var snapshot = JsonSerializer.Deserialize<RuntimeStatus>(snapshotJson, JsonOptions.Default);
                if (snapshot is not null)
                {
                    return snapshot;
                }
            }
            catch (JsonException)
            {
                // Fall back to the scalar columns if the stored snapshot no longer matches the current contract.
            }
        }

        return ReadLegacyStatus(reader);
    }

    public async Task SaveAsync(RuntimeStatus status, CancellationToken cancellationToken)
    {
        await UpsertAsync(status, requireNotInProgress: false, cancellationToken);
    }

    public async Task<bool> TryStartAsync(RuntimeStatus status, CancellationToken cancellationToken)
    {
        return await UpsertAsync(status, requireNotInProgress: true, cancellationToken) > 0;
    }

    private async Task<int> UpsertAsync(RuntimeStatus status, bool requireNotInProgress, CancellationToken cancellationToken)
    {
        var databasePath = pathResolver.ResolveConfiguredPath() ?? pathResolver.Resolve();
        if (string.IsNullOrWhiteSpace(databasePath))
        {
            return 0;
        }

        await using var connection = OpenWriteConnection(databasePath);
        await connection.OpenAsync(cancellationToken);

        await using var upsertCommand = connection.CreateCommand();
        upsertCommand.CommandText =
            requireNotInProgress
                ? """
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
            VALUES (
              $statePath,
              $runId,
              $status,
              $stage,
              $startedAt,
              $lastUpdatedAt,
              $completedAt,
              $currentWorkerId,
              $lastAction,
              $processedWorkers,
              $totalWorkers,
              $errorMessage,
              $snapshotJson
            )
            ON CONFLICT(state_path) DO UPDATE SET
              run_id = excluded.run_id,
              status = excluded.status,
              stage = excluded.stage,
              started_at = excluded.started_at,
              last_updated_at = excluded.last_updated_at,
              completed_at = excluded.completed_at,
              current_worker_id = excluded.current_worker_id,
              last_action = excluded.last_action,
              processed_workers = excluded.processed_workers,
              total_workers = excluded.total_workers,
              error_message = excluded.error_message,
              snapshot_json = excluded.snapshot_json
            WHERE runtime_status.status IS NULL OR runtime_status.status != 'InProgress';
            """
                : """
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
            VALUES (
              $statePath,
              $runId,
              $status,
              $stage,
              $startedAt,
              $lastUpdatedAt,
              $completedAt,
              $currentWorkerId,
              $lastAction,
              $processedWorkers,
              $totalWorkers,
              $errorMessage,
              $snapshotJson
            )
            ON CONFLICT(state_path) DO UPDATE SET
              run_id = excluded.run_id,
              status = excluded.status,
              stage = excluded.stage,
              started_at = excluded.started_at,
              last_updated_at = excluded.last_updated_at,
              completed_at = excluded.completed_at,
              current_worker_id = excluded.current_worker_id,
              last_action = excluded.last_action,
              processed_workers = excluded.processed_workers,
              total_workers = excluded.total_workers,
              error_message = excluded.error_message,
              snapshot_json = excluded.snapshot_json;
            """;
        BindStatusParameters(upsertCommand, status);
        return await upsertCommand.ExecuteNonQueryAsync(cancellationToken);
    }

    private static void BindStatusParameters(SqliteCommand command, RuntimeStatus status)
    {
        command.Parameters.AddWithValue("$statePath", CurrentStatePath);
        command.Parameters.AddWithValue("$runId", (object?)status.RunId ?? DBNull.Value);
        command.Parameters.AddWithValue("$status", status.Status);
        command.Parameters.AddWithValue("$stage", status.Stage);
        command.Parameters.AddWithValue("$startedAt", ToDbValue(status.StartedAt));
        command.Parameters.AddWithValue("$lastUpdatedAt", ToDbValue(status.LastUpdatedAt));
        command.Parameters.AddWithValue("$completedAt", ToDbValue(status.CompletedAt));
        command.Parameters.AddWithValue("$currentWorkerId", (object?)status.CurrentWorkerId ?? DBNull.Value);
        command.Parameters.AddWithValue("$lastAction", (object?)status.LastAction ?? DBNull.Value);
        command.Parameters.AddWithValue("$processedWorkers", status.ProcessedWorkers);
        command.Parameters.AddWithValue("$totalWorkers", status.TotalWorkers);
        command.Parameters.AddWithValue("$errorMessage", (object?)status.ErrorMessage ?? DBNull.Value);
        command.Parameters.AddWithValue("$snapshotJson", JsonSerializer.Serialize(status, JsonOptions.Default));
    }

    private static RuntimeStatus ReadLegacyStatus(SqliteDataReader reader)
    {
        return new RuntimeStatus(
            Status: reader.GetStringOrDefault("status") ?? "Idle",
            Stage: reader.GetStringOrDefault("stage") ?? "NotStarted",
            RunId: reader.GetStringOrDefault("run_id"),
            Mode: null,
            DryRun: false,
            ProcessedWorkers: reader.GetInt32OrDefault("processed_workers"),
            TotalWorkers: reader.GetInt32OrDefault("total_workers"),
            CurrentWorkerId: reader.GetStringOrDefault("current_worker_id"),
            LastAction: reader.GetStringOrDefault("last_action"),
            StartedAt: ParseDate(reader.GetStringOrDefault("started_at")),
            LastUpdatedAt: ParseDate(reader.GetStringOrDefault("last_updated_at")),
            CompletedAt: ParseDate(reader.GetStringOrDefault("completed_at")),
            ErrorMessage: reader.GetStringOrDefault("error_message"));
    }

    private static DateTimeOffset? ParseDate(string? value)
    {
        return DateTimeOffset.TryParse(value, out var parsed) ? parsed : null;
    }

    private static SqliteConnection OpenConnection(string databasePath)
    {
        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadOnly,
        }.ToString();
        return new SqliteConnection(connectionString);
    }

    private static SqliteConnection OpenWriteConnection(string databasePath)
    {
        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
        }.ToString();
        return new SqliteConnection(connectionString);
    }

    private static object ToDbValue(DateTimeOffset? value) => value?.ToString("O") ?? (object)DBNull.Value;
}

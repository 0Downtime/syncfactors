using Microsoft.Data.Sqlite;
using SyncFactors.Contracts;
using SyncFactors.Domain;

namespace SyncFactors.Infrastructure;

public sealed class SqliteRuntimeStatusStore(SqlitePathResolver pathResolver) : IRuntimeStatusStore
{
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

    public async Task SaveAsync(RuntimeStatus status, CancellationToken cancellationToken)
    {
        var databasePath = pathResolver.ResolveConfiguredPath() ?? pathResolver.Resolve();
        if (string.IsNullOrWhiteSpace(databasePath))
        {
            return;
        }

        await using var connection = OpenWriteConnection(databasePath);
        await connection.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        await using (var deleteCommand = connection.CreateCommand())
        {
            deleteCommand.Transaction = (SqliteTransaction)transaction;
            deleteCommand.CommandText = "DELETE FROM runtime_status;";
            await deleteCommand.ExecuteNonQueryAsync(cancellationToken);
        }

        await using (var insertCommand = connection.CreateCommand())
        {
            insertCommand.Transaction = (SqliteTransaction)transaction;
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
                  $errorMessage
                );
                """;
            insertCommand.Parameters.AddWithValue("$runId", (object?)status.RunId ?? DBNull.Value);
            insertCommand.Parameters.AddWithValue("$status", status.Status);
            insertCommand.Parameters.AddWithValue("$stage", status.Stage);
            insertCommand.Parameters.AddWithValue("$startedAt", ToDbValue(status.StartedAt));
            insertCommand.Parameters.AddWithValue("$lastUpdatedAt", ToDbValue(status.LastUpdatedAt));
            insertCommand.Parameters.AddWithValue("$completedAt", ToDbValue(status.CompletedAt));
            insertCommand.Parameters.AddWithValue("$currentWorkerId", (object?)status.CurrentWorkerId ?? DBNull.Value);
            insertCommand.Parameters.AddWithValue("$lastAction", (object?)status.LastAction ?? DBNull.Value);
            insertCommand.Parameters.AddWithValue("$processedWorkers", status.ProcessedWorkers);
            insertCommand.Parameters.AddWithValue("$totalWorkers", status.TotalWorkers);
            insertCommand.Parameters.AddWithValue("$errorMessage", (object?)status.ErrorMessage ?? DBNull.Value);
            await insertCommand.ExecuteNonQueryAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
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

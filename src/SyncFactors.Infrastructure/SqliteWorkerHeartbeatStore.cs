using Microsoft.Data.Sqlite;
using SyncFactors.Contracts;
using SyncFactors.Domain;

namespace SyncFactors.Infrastructure;

public sealed class SqliteWorkerHeartbeatStore(SqlitePathResolver pathResolver) : IWorkerHeartbeatStore
{
    public async Task<WorkerHeartbeat?> GetCurrentAsync(CancellationToken cancellationToken)
    {
        var databasePath = pathResolver.Resolve();
        if (string.IsNullOrWhiteSpace(databasePath))
        {
            return null;
        }

        await using var connection = OpenConnection(databasePath, SqliteOpenMode.ReadWriteCreate);
        await connection.OpenAsync(cancellationToken);

        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT
              service,
              state,
              activity,
              started_at,
              last_seen_at
            FROM worker_heartbeat
            ORDER BY last_seen_at DESC
            LIMIT 1;
            """;

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        return new WorkerHeartbeat(
            Service: reader.GetStringOrDefault("service") ?? "SyncFactors.Worker",
            State: reader.GetStringOrDefault("state") ?? "Unknown",
            Activity: reader.GetStringOrDefault("activity"),
            StartedAt: ParseDate(reader.GetStringOrDefault("started_at")) ?? DateTimeOffset.MinValue,
            LastSeenAt: ParseDate(reader.GetStringOrDefault("last_seen_at")) ?? DateTimeOffset.MinValue);
    }

    public async Task SaveAsync(WorkerHeartbeat heartbeat, CancellationToken cancellationToken)
    {
        var databasePath = pathResolver.ResolveConfiguredPath() ?? pathResolver.Resolve();
        if (string.IsNullOrWhiteSpace(databasePath))
        {
            return;
        }

        await using var connection = OpenConnection(databasePath, SqliteOpenMode.ReadWriteCreate);
        await connection.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        await using (var deleteCommand = connection.CreateCommand())
        {
            deleteCommand.Transaction = (SqliteTransaction)transaction;
            deleteCommand.CommandText = "DELETE FROM worker_heartbeat;";
            await deleteCommand.ExecuteNonQueryAsync(cancellationToken);
        }

        await using (var insertCommand = connection.CreateCommand())
        {
            insertCommand.Transaction = (SqliteTransaction)transaction;
            insertCommand.CommandText =
                """
                INSERT INTO worker_heartbeat (
                  service,
                  state,
                  activity,
                  started_at,
                  last_seen_at
                )
                VALUES (
                  $service,
                  $state,
                  $activity,
                  $startedAt,
                  $lastSeenAt
                );
                """;
            insertCommand.Parameters.AddWithValue("$service", heartbeat.Service);
            insertCommand.Parameters.AddWithValue("$state", heartbeat.State);
            insertCommand.Parameters.AddWithValue("$activity", (object?)heartbeat.Activity ?? DBNull.Value);
            insertCommand.Parameters.AddWithValue("$startedAt", heartbeat.StartedAt.ToString("O"));
            insertCommand.Parameters.AddWithValue("$lastSeenAt", heartbeat.LastSeenAt.ToString("O"));
            await insertCommand.ExecuteNonQueryAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
    }

    private static SqliteConnection OpenConnection(string databasePath, SqliteOpenMode mode)
    {
        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = mode,
        }.ToString();

        return new SqliteConnection(connectionString);
    }

    private static DateTimeOffset? ParseDate(string? value)
    {
        return DateTimeOffset.TryParse(value, out var parsed) ? parsed : null;
    }
}

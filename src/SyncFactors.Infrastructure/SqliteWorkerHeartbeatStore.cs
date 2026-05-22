using Microsoft.Data.Sqlite;
using SyncFactors.Contracts;
using SyncFactors.Domain;

namespace SyncFactors.Infrastructure;

public sealed class SqliteWorkerHeartbeatStore(SqlitePathResolver pathResolver) : IWorkerHeartbeatStore
{
    private static readonly TimeSpan[] BusyRetryDelays =
    [
        TimeSpan.FromMilliseconds(100),
        TimeSpan.FromMilliseconds(250),
        TimeSpan.FromMilliseconds(500),
        TimeSpan.FromSeconds(1),
        TimeSpan.FromSeconds(2)
    ];

    public async Task<WorkerHeartbeat?> GetCurrentAsync(CancellationToken cancellationToken)
    {
        var databasePath = pathResolver.Resolve();
        if (string.IsNullOrWhiteSpace(databasePath))
        {
            return null;
        }

        await using var connection = OpenConnection(databasePath, SqliteOpenMode.ReadOnly);
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

        await SaveWithRetryAsync(databasePath, heartbeat, cancellationToken);
    }

    private static async Task SaveWithRetryAsync(string databasePath, WorkerHeartbeat heartbeat, CancellationToken cancellationToken)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                await SaveOnceAsync(databasePath, heartbeat, cancellationToken);
                return;
            }
            catch (SqliteException ex) when (SqliteConnections.IsBusyOrLocked(ex) && attempt < BusyRetryDelays.Length)
            {
                await Task.Delay(BusyRetryDelays[attempt], cancellationToken);
            }
        }
    }

    private static async Task SaveOnceAsync(string databasePath, WorkerHeartbeat heartbeat, CancellationToken cancellationToken)
    {
        await using var connection = OpenConnection(databasePath, SqliteOpenMode.ReadWriteCreate);
        await connection.OpenAsync(cancellationToken);

        await using var command = connection.CreateCommand();
        command.CommandText =
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
            )
            ON CONFLICT(service) DO UPDATE SET
              state = excluded.state,
              activity = excluded.activity,
              started_at = excluded.started_at,
              last_seen_at = excluded.last_seen_at;
            """;
        command.Parameters.AddWithValue("$service", heartbeat.Service);
        command.Parameters.AddWithValue("$state", heartbeat.State);
        command.Parameters.AddWithValue("$activity", (object?)heartbeat.Activity ?? DBNull.Value);
        command.Parameters.AddWithValue("$startedAt", heartbeat.StartedAt.ToString("O"));
        command.Parameters.AddWithValue("$lastSeenAt", heartbeat.LastSeenAt.ToString("O"));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static SqliteConnection OpenConnection(string databasePath, SqliteOpenMode mode)
    {
        return SqliteConnections.Open(databasePath, mode);
    }

    private static DateTimeOffset? ParseDate(string? value)
    {
        return DateTimeOffset.TryParse(value, out var parsed) ? parsed : null;
    }
}

using Microsoft.Data.Sqlite;
using SyncFactors.Domain;

namespace SyncFactors.Infrastructure;

public sealed class SqliteDeltaSyncStateStore(SqlitePathResolver pathResolver) : IDeltaSyncStateStore
{
    public async Task<DateTimeOffset?> GetCheckpointAsync(string syncKey, CancellationToken cancellationToken)
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
            SELECT checkpoint_utc
            FROM delta_sync_state
            WHERE sync_key = $syncKey
            LIMIT 1;
            """;
        command.Parameters.AddWithValue("$syncKey", syncKey);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        return ParseDate(reader.GetStringOrDefault("checkpoint_utc"));
    }

    public async Task SaveCheckpointAsync(string syncKey, DateTimeOffset checkpointUtc, CancellationToken cancellationToken)
    {
        var databasePath = pathResolver.ResolveConfiguredPath() ?? pathResolver.Resolve();
        if (string.IsNullOrWhiteSpace(databasePath))
        {
            return;
        }

        await using var connection = OpenConnection(databasePath, SqliteOpenMode.ReadWriteCreate);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT INTO delta_sync_state (
              sync_key,
              checkpoint_utc,
              updated_at
            )
            VALUES (
              $syncKey,
              $checkpointUtc,
              $updatedAt
            )
            ON CONFLICT(sync_key) DO UPDATE SET
              checkpoint_utc = excluded.checkpoint_utc,
              updated_at = excluded.updated_at;
            """;
        command.Parameters.AddWithValue("$syncKey", syncKey);
        command.Parameters.AddWithValue("$checkpointUtc", checkpointUtc.UtcDateTime.ToString("O"));
        command.Parameters.AddWithValue("$updatedAt", DateTimeOffset.UtcNow.ToString("O"));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static SqliteConnection OpenConnection(string databasePath, SqliteOpenMode mode)
    {
        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = mode
        }.ToString();
        return new SqliteConnection(connectionString);
    }

    private static DateTimeOffset? ParseDate(string? value)
    {
        return DateTimeOffset.TryParse(value, out var parsed) ? parsed : null;
    }
}

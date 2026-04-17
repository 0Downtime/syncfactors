using Microsoft.Data.Sqlite;
using SyncFactors.Domain;

namespace SyncFactors.Infrastructure;

public sealed class SqliteDashboardSettingsStore(SqlitePathResolver pathResolver) : IDashboardSettingsStore
{
    private const string SettingsKey = "global";

    public async Task<bool?> GetHealthProbesEnabledOverrideAsync(CancellationToken cancellationToken)
    {
        var databasePath = pathResolver.ResolveConfiguredPath() ?? pathResolver.Resolve();
        if (string.IsNullOrWhiteSpace(databasePath))
        {
            return null;
        }

        await using var connection = OpenConnection(databasePath);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT health_probes_enabled
            FROM dashboard_settings
            WHERE settings_key = $settingsKey
            LIMIT 1;
            """;
        command.Parameters.AddWithValue("$settingsKey", SettingsKey);

        var result = await command.ExecuteScalarAsync(cancellationToken);
        return result is null || result is DBNull
            ? null
            : Convert.ToInt32(result) != 0;
    }

    public async Task<int?> GetHealthProbeIntervalSecondsOverrideAsync(CancellationToken cancellationToken)
    {
        var databasePath = pathResolver.ResolveConfiguredPath() ?? pathResolver.Resolve();
        if (string.IsNullOrWhiteSpace(databasePath))
        {
            return null;
        }

        await using var connection = OpenConnection(databasePath);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT health_probe_interval_seconds
            FROM dashboard_settings
            WHERE settings_key = $settingsKey
            LIMIT 1;
            """;
        command.Parameters.AddWithValue("$settingsKey", SettingsKey);

        var result = await command.ExecuteScalarAsync(cancellationToken);
        return result is null || result is DBNull
            ? null
            : Convert.ToInt32(result);
    }

    public async Task SaveHealthProbeOverrideAsync(bool enabled, int intervalSeconds, CancellationToken cancellationToken)
    {
        var databasePath = pathResolver.ResolveConfiguredPath() ?? pathResolver.Resolve();
        if (string.IsNullOrWhiteSpace(databasePath))
        {
            return;
        }

        await using var connection = OpenConnection(databasePath);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT INTO dashboard_settings (
              settings_key,
              health_probes_enabled,
              health_probe_interval_seconds,
              updated_at
            )
            VALUES (
              $settingsKey,
              $healthProbesEnabled,
              $healthProbeIntervalSeconds,
              $updatedAt
            )
            ON CONFLICT(settings_key) DO UPDATE SET
              health_probes_enabled = excluded.health_probes_enabled,
              health_probe_interval_seconds = excluded.health_probe_interval_seconds,
              updated_at = excluded.updated_at;
            """;
        command.Parameters.AddWithValue("$settingsKey", SettingsKey);
        command.Parameters.AddWithValue("$healthProbesEnabled", enabled ? 1 : 0);
        command.Parameters.AddWithValue("$healthProbeIntervalSeconds", intervalSeconds);
        command.Parameters.AddWithValue("$updatedAt", DateTimeOffset.UtcNow.ToString("O"));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static SqliteConnection OpenConnection(string databasePath)
    {
        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadWriteCreate
        }.ToString();
        return new SqliteConnection(connectionString);
    }
}

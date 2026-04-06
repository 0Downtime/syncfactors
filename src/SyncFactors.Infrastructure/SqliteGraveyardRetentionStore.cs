using Microsoft.Data.Sqlite;
using SyncFactors.Contracts;
using SyncFactors.Domain;

namespace SyncFactors.Infrastructure;

public sealed class SqliteGraveyardRetentionStore(SqlitePathResolver pathResolver) : IGraveyardRetentionStore
{
    private const string ReportKey = "graveyardRetention";

    public async Task UpsertObservedAsync(GraveyardRetentionRecord record, CancellationToken cancellationToken)
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
            INSERT INTO graveyard_retention (
              worker_id,
              sam_account_name,
              display_name,
              distinguished_name,
              status,
              end_date_utc,
              last_observed_at_utc,
              active
            )
            VALUES (
              $workerId,
              $samAccountName,
              $displayName,
              $distinguishedName,
              $status,
              $endDateUtc,
              $lastObservedAtUtc,
              $active
            )
            ON CONFLICT(worker_id) DO UPDATE SET
              sam_account_name = excluded.sam_account_name,
              display_name = excluded.display_name,
              distinguished_name = excluded.distinguished_name,
              status = excluded.status,
              end_date_utc = excluded.end_date_utc,
              last_observed_at_utc = excluded.last_observed_at_utc,
              active = excluded.active;
            """;
        command.Parameters.AddWithValue("$workerId", record.WorkerId);
        command.Parameters.AddWithValue("$samAccountName", (object?)record.SamAccountName ?? DBNull.Value);
        command.Parameters.AddWithValue("$displayName", (object?)record.DisplayName ?? DBNull.Value);
        command.Parameters.AddWithValue("$distinguishedName", (object?)record.DistinguishedName ?? DBNull.Value);
        command.Parameters.AddWithValue("$status", record.Status);
        command.Parameters.AddWithValue("$endDateUtc", ToDbValue(record.EndDateUtc));
        command.Parameters.AddWithValue("$lastObservedAtUtc", record.LastObservedAtUtc.ToString("O"));
        command.Parameters.AddWithValue("$active", record.Active ? 1 : 0);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task ResolveAsync(string workerId, CancellationToken cancellationToken)
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
            UPDATE graveyard_retention
            SET active = 0
            WHERE worker_id = $workerId;
            """;
        command.Parameters.AddWithValue("$workerId", workerId);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<GraveyardRetentionRecord>> ListActiveAsync(CancellationToken cancellationToken)
    {
        var databasePath = pathResolver.ResolveConfiguredPath() ?? pathResolver.Resolve();
        if (string.IsNullOrWhiteSpace(databasePath))
        {
            return [];
        }

        await using var connection = OpenConnection(databasePath);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT worker_id, sam_account_name, display_name, distinguished_name, status, end_date_utc, last_observed_at_utc, active
            FROM graveyard_retention
            WHERE active = 1;
            """;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var records = new List<GraveyardRetentionRecord>();
        while (await reader.ReadAsync(cancellationToken))
        {
            records.Add(new GraveyardRetentionRecord(
                WorkerId: reader.GetString(0),
                SamAccountName: reader.IsDBNull(1) ? null : reader.GetString(1),
                DisplayName: reader.IsDBNull(2) ? null : reader.GetString(2),
                DistinguishedName: reader.IsDBNull(3) ? null : reader.GetString(3),
                Status: reader.GetString(4),
                EndDateUtc: ParseDate(reader.IsDBNull(5) ? null : reader.GetString(5)),
                LastObservedAtUtc: ParseDate(reader.GetString(6)) ?? DateTimeOffset.MinValue,
                Active: reader.GetInt32(7) != 0));
        }

        return records;
    }

    public async Task<GraveyardRetentionReportStatus> GetReportStatusAsync(CancellationToken cancellationToken)
    {
        var databasePath = pathResolver.ResolveConfiguredPath() ?? pathResolver.Resolve();
        if (string.IsNullOrWhiteSpace(databasePath))
        {
            return new GraveyardRetentionReportStatus(null, null, null);
        }

        await using var connection = OpenConnection(databasePath);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT last_sent_at_utc, last_attempted_at_utc, last_error
            FROM graveyard_retention_report_state
            WHERE report_key = $reportKey
            LIMIT 1;
            """;
        command.Parameters.AddWithValue("$reportKey", ReportKey);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return new GraveyardRetentionReportStatus(null, null, null);
        }

        return new GraveyardRetentionReportStatus(
            LastSentAtUtc: ParseDate(reader.IsDBNull(0) ? null : reader.GetString(0)),
            LastAttemptedAtUtc: ParseDate(reader.IsDBNull(1) ? null : reader.GetString(1)),
            LastError: reader.IsDBNull(2) ? null : reader.GetString(2));
    }

    public async Task RecordReportAttemptAsync(DateTimeOffset attemptedAt, string? error, DateTimeOffset? sentAtUtc, CancellationToken cancellationToken)
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
            INSERT INTO graveyard_retention_report_state (
              report_key,
              last_sent_at_utc,
              last_attempted_at_utc,
              last_error
            )
            VALUES (
              $reportKey,
              $lastSentAtUtc,
              $lastAttemptedAtUtc,
              $lastError
            )
            ON CONFLICT(report_key) DO UPDATE SET
              last_sent_at_utc = excluded.last_sent_at_utc,
              last_attempted_at_utc = excluded.last_attempted_at_utc,
              last_error = excluded.last_error;
            """;
        command.Parameters.AddWithValue("$reportKey", ReportKey);
        command.Parameters.AddWithValue("$lastSentAtUtc", ToDbValue(sentAtUtc));
        command.Parameters.AddWithValue("$lastAttemptedAtUtc", attemptedAt.ToString("O"));
        command.Parameters.AddWithValue("$lastError", (object?)error ?? DBNull.Value);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static object ToDbValue(DateTimeOffset? value) => value?.ToString("O") ?? (object)DBNull.Value;

    private static DateTimeOffset? ParseDate(string? value) => DateTimeOffset.TryParse(value, out var parsed) ? parsed : null;

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

using Microsoft.Data.Sqlite;
using SyncFactors.Contracts;
using SyncFactors.Domain;

namespace SyncFactors.Infrastructure;

public sealed class SqliteSyncScheduleStore(SqlitePathResolver pathResolver, TimeProvider timeProvider) : ISyncScheduleStore
{
    private const string ScheduleKey = "global";
    private const int DefaultIntervalMinutes = 30;
    private const int MinIntervalMinutes = 5;
    private const int MaxIntervalMinutes = 1440;

    public async Task<SyncScheduleStatus> GetCurrentAsync(CancellationToken cancellationToken)
    {
        var databasePath = pathResolver.ResolveConfiguredPath() ?? pathResolver.Resolve();
        if (string.IsNullOrWhiteSpace(databasePath))
        {
            return DefaultSchedule();
        }

        await using var connection = OpenConnection(databasePath);
        await connection.OpenAsync(cancellationToken);
        return await ReadCurrentAsync(connection, cancellationToken) ?? DefaultSchedule();
    }

    public async Task<SyncScheduleStatus> UpdateAsync(UpdateSyncScheduleRequest request, CancellationToken cancellationToken)
    {
        var intervalMinutes = ClampIntervalMinutes(request.IntervalMinutes);
        var now = timeProvider.GetUtcNow();
        var current = await GetCurrentAsync(cancellationToken);
        var updated = current with
        {
            Enabled = request.Enabled,
            IntervalMinutes = intervalMinutes,
            NextRunAt = request.Enabled ? now.AddMinutes(intervalMinutes) : null,
            LastEnqueueError = null
        };

        await SaveAsync(updated, cancellationToken);
        return updated;
    }

    public async Task<SyncScheduleStatus> RecordSuccessfulEnqueueAsync(DateTimeOffset enqueuedAt, CancellationToken cancellationToken)
    {
        var current = await GetCurrentAsync(cancellationToken);
        var updated = current with
        {
            NextRunAt = current.Enabled ? enqueuedAt.AddMinutes(current.IntervalMinutes) : null,
            LastScheduledRunAt = enqueuedAt,
            LastEnqueueAttemptAt = enqueuedAt,
            LastEnqueueError = null
        };

        await SaveAsync(updated, cancellationToken);
        return updated;
    }

    public async Task<SyncScheduleStatus> RecordFailedEnqueueAsync(DateTimeOffset attemptedAt, string errorMessage, CancellationToken cancellationToken)
    {
        var current = await GetCurrentAsync(cancellationToken);
        var updated = current with
        {
            LastEnqueueAttemptAt = attemptedAt,
            LastEnqueueError = errorMessage
        };

        await SaveAsync(updated, cancellationToken);
        return updated;
    }

    private async Task SaveAsync(SyncScheduleStatus schedule, CancellationToken cancellationToken)
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
            INSERT INTO sync_schedule (
              schedule_key,
              enabled,
              interval_minutes,
              next_run_at,
              last_scheduled_run_at,
              last_enqueue_attempt_at,
              last_enqueue_error
            )
            VALUES (
              $scheduleKey,
              $enabled,
              $intervalMinutes,
              $nextRunAt,
              $lastScheduledRunAt,
              $lastEnqueueAttemptAt,
              $lastEnqueueError
            )
            ON CONFLICT(schedule_key) DO UPDATE SET
              enabled = excluded.enabled,
              interval_minutes = excluded.interval_minutes,
              next_run_at = excluded.next_run_at,
              last_scheduled_run_at = excluded.last_scheduled_run_at,
              last_enqueue_attempt_at = excluded.last_enqueue_attempt_at,
              last_enqueue_error = excluded.last_enqueue_error;
            """;
        command.Parameters.AddWithValue("$scheduleKey", ScheduleKey);
        command.Parameters.AddWithValue("$enabled", schedule.Enabled ? 1 : 0);
        command.Parameters.AddWithValue("$intervalMinutes", ClampIntervalMinutes(schedule.IntervalMinutes));
        command.Parameters.AddWithValue("$nextRunAt", ToDbValue(schedule.NextRunAt));
        command.Parameters.AddWithValue("$lastScheduledRunAt", ToDbValue(schedule.LastScheduledRunAt));
        command.Parameters.AddWithValue("$lastEnqueueAttemptAt", ToDbValue(schedule.LastEnqueueAttemptAt));
        command.Parameters.AddWithValue("$lastEnqueueError", (object?)schedule.LastEnqueueError ?? DBNull.Value);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static SyncScheduleStatus? Map(SqliteDataReader reader)
    {
        return new SyncScheduleStatus(
            Enabled: reader.GetInt32OrDefault("enabled") != 0,
            IntervalMinutes: ClampIntervalMinutes(reader.GetInt32OrDefault("interval_minutes") == 0 ? DefaultIntervalMinutes : reader.GetInt32OrDefault("interval_minutes")),
            NextRunAt: ParseDate(reader.GetStringOrDefault("next_run_at")),
            LastScheduledRunAt: ParseDate(reader.GetStringOrDefault("last_scheduled_run_at")),
            LastEnqueueAttemptAt: ParseDate(reader.GetStringOrDefault("last_enqueue_attempt_at")),
            LastEnqueueError: reader.GetStringOrDefault("last_enqueue_error"));
    }

    private static SyncScheduleStatus DefaultSchedule()
    {
        return new SyncScheduleStatus(
            Enabled: false,
            IntervalMinutes: DefaultIntervalMinutes,
            NextRunAt: null,
            LastScheduledRunAt: null,
            LastEnqueueAttemptAt: null,
            LastEnqueueError: null);
    }

    private static int ClampIntervalMinutes(int intervalMinutes)
    {
        if (intervalMinutes < MinIntervalMinutes)
        {
            return MinIntervalMinutes;
        }

        if (intervalMinutes > MaxIntervalMinutes)
        {
            return MaxIntervalMinutes;
        }

        return intervalMinutes;
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

    private static async Task<SyncScheduleStatus?> ReadCurrentAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT enabled, interval_minutes, next_run_at, last_scheduled_run_at, last_enqueue_attempt_at, last_enqueue_error
            FROM sync_schedule
            WHERE schedule_key = $scheduleKey
            LIMIT 1;
            """;
        command.Parameters.AddWithValue("$scheduleKey", ScheduleKey);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? Map(reader) : null;
    }
}

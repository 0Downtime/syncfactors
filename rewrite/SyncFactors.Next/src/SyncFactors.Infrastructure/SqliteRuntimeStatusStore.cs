using SyncFactors.Contracts;
using SyncFactors.Domain;

namespace SyncFactors.Infrastructure;

public sealed class SqliteRuntimeStatusStore(SqlitePathResolver pathResolver, SqliteJsonShell sqlite) : IRuntimeStatusStore
{
    public async Task<RuntimeStatus?> GetCurrentAsync(CancellationToken cancellationToken)
    {
        var databasePath = pathResolver.Resolve();
        if (string.IsNullOrWhiteSpace(databasePath))
        {
            return null;
        }

        var rows = await sqlite.QueryAsync<RuntimeStatusRow>(
            databasePath,
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
            """,
            cancellationToken);

        var row = rows.FirstOrDefault();
        if (row is null)
        {
            return null;
        }

        return new RuntimeStatus(
            Status: row.Status ?? "Idle",
            Stage: row.Stage ?? "NotStarted",
            RunId: row.RunId,
            Mode: null,
            DryRun: false,
            ProcessedWorkers: row.ProcessedWorkers,
            TotalWorkers: row.TotalWorkers,
            CurrentWorkerId: row.CurrentWorkerId,
            LastAction: row.LastAction,
            StartedAt: ParseDate(row.StartedAt),
            LastUpdatedAt: ParseDate(row.LastUpdatedAt),
            CompletedAt: ParseDate(row.CompletedAt),
            ErrorMessage: row.ErrorMessage);
    }

    private static DateTimeOffset? ParseDate(string? value)
    {
        return DateTimeOffset.TryParse(value, out var parsed) ? parsed : null;
    }

    private sealed class RuntimeStatusRow
    {
        public string? RunId { get; init; }
        public string? Status { get; init; }
        public string? Stage { get; init; }
        public string? StartedAt { get; init; }
        public string? LastUpdatedAt { get; init; }
        public string? CompletedAt { get; init; }
        public string? CurrentWorkerId { get; init; }
        public string? LastAction { get; init; }
        public int ProcessedWorkers { get; init; }
        public int TotalWorkers { get; init; }
        public string? ErrorMessage { get; init; }
    }
}

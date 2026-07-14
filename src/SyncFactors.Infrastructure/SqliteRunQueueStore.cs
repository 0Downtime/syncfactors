using Microsoft.Data.Sqlite;
using SyncFactors.Contracts;
using SyncFactors.Domain;

namespace SyncFactors.Infrastructure;

public sealed class SqliteRunQueueStore(SqlitePathResolver pathResolver) : IRunQueueStore
{
    private static readonly string[] PendingOrActiveStatuses = ["Pending", "InProgress", "CancelRequested"];

    public async Task<RunQueueRequest> EnqueueAsync(StartRunRequest request, CancellationToken cancellationToken)
    {
        var databasePath = pathResolver.ResolveConfiguredPath() ?? pathResolver.Resolve();
        if (string.IsNullOrWhiteSpace(databasePath))
        {
            throw new InvalidOperationException("SQLite path could not be resolved.");
        }

        var queued = new RunQueueRequest(
            RequestId: $"runreq-{Guid.NewGuid():N}",
            Mode: string.IsNullOrWhiteSpace(request.Mode) ? "BulkSync" : request.Mode,
            DryRun: request.DryRun,
            RunTrigger: string.IsNullOrWhiteSpace(request.RunTrigger) ? "AdHoc" : request.RunTrigger,
            RequestedBy: request.RequestedBy,
            Status: "Pending",
            RequestedAt: DateTimeOffset.UtcNow,
            StartedAt: null,
            CompletedAt: null,
            RunId: null,
            ErrorMessage: null);

        await using var connection = SqliteConnections.Open(databasePath);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT INTO run_queue (
              request_id,
              mode,
              dry_run,
              run_trigger,
              requested_by,
              status,
              requested_at,
              started_at,
              completed_at,
              run_id,
              worker_name,
              error_message
            )
            VALUES (
              $requestId,
              $mode,
              $dryRun,
              $runTrigger,
              $requestedBy,
              $status,
              $requestedAt,
              NULL,
              NULL,
              NULL,
              NULL,
              NULL
            );
            """;
        Bind(command, queued, workerName: null);
        try
        {
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        catch (SqliteException ex) when (ex.SqliteErrorCode == 19)
        {
            throw new RunQueueConflictException();
        }
        return queued;
    }

    public async Task<RunQueueRequest?> ClaimNextPendingAsync(string workerName, CancellationToken cancellationToken)
    {
        var databasePath = pathResolver.ResolveConfiguredPath() ?? pathResolver.Resolve();
        if (string.IsNullOrWhiteSpace(databasePath))
        {
            return null;
        }

        await using var connection = SqliteConnections.Open(databasePath);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            UPDATE run_queue
            SET status = 'InProgress',
                started_at = $startedAt,
                worker_name = $workerName
            WHERE request_id = (
                SELECT request_id
                FROM run_queue
                WHERE status = 'Pending'
                ORDER BY requested_at ASC, request_id ASC
                LIMIT 1
            )
              AND status = 'Pending'
              AND NOT EXISTS (
                SELECT 1
                FROM run_queue
                WHERE status IN ('InProgress', 'CancelRequested')
            )
            RETURNING request_id, mode, dry_run, run_trigger, requested_by, status, requested_at, started_at, completed_at, run_id, error_message;
            """;
        command.Parameters.AddWithValue("$startedAt", DateTimeOffset.UtcNow.ToString("O"));
        command.Parameters.AddWithValue("$workerName", workerName);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken)
            ? Map(reader)
            : null;
    }

    public async Task<bool> HasPendingOrActiveRunAsync(CancellationToken cancellationToken)
    {
        return await GetPendingOrActiveAsync(cancellationToken) is not null;
    }

    public async Task<RunQueueRequest?> GetAsync(string requestId, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(requestId))
        {
            return null;
        }

        var databasePath = pathResolver.ResolveConfiguredPath() ?? pathResolver.Resolve();
        if (string.IsNullOrWhiteSpace(databasePath))
        {
            return null;
        }

        await using var connection = SqliteConnections.Open(databasePath);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT request_id, mode, dry_run, run_trigger, requested_by, status, requested_at, started_at, completed_at, run_id, error_message
            FROM run_queue
            WHERE request_id = $requestId
            LIMIT 1;
            """;
        command.Parameters.AddWithValue("$requestId", requestId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken)
            ? Map(reader)
            : null;
    }

    public async Task<RunQueueRequest?> GetPendingOrActiveAsync(CancellationToken cancellationToken)
    {
        var databasePath = pathResolver.ResolveConfiguredPath() ?? pathResolver.Resolve();
        if (string.IsNullOrWhiteSpace(databasePath))
        {
            return null;
        }

        await using var connection = SqliteConnections.Open(databasePath);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT request_id, mode, dry_run, run_trigger, requested_by, status, requested_at, started_at, completed_at, run_id, error_message
            FROM run_queue
            WHERE status IN ('Pending', 'InProgress', 'CancelRequested')
            ORDER BY CASE status
                WHEN 'InProgress' THEN 0
                WHEN 'CancelRequested' THEN 1
                ELSE 2
            END, requested_at ASC
            LIMIT 1;
            """;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken)
            ? Map(reader)
            : null;
    }

    public async Task<bool> CancelPendingOrActiveAsync(string? requestedBy, CancellationToken cancellationToken)
    {
        var databasePath = pathResolver.ResolveConfiguredPath() ?? pathResolver.Resolve();
        if (string.IsNullOrWhiteSpace(databasePath))
        {
            return false;
        }

        await using var connection = SqliteConnections.Open(databasePath);
        await connection.OpenAsync(cancellationToken);
        var cancellationMessage = string.IsNullOrWhiteSpace(requestedBy)
            ? "Cancellation requested."
            : $"Cancellation requested by {requestedBy}.";

        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            UPDATE run_queue
            SET status = CASE status
                    WHEN 'Pending' THEN 'Canceled'
                    ELSE 'CancelRequested'
                END,
                completed_at = CASE status
                    WHEN 'Pending' THEN $completedAt
                    ELSE completed_at
                END,
                error_message = $errorMessage
            WHERE request_id = (
                SELECT request_id
                FROM run_queue
                WHERE status IN ('Pending', 'InProgress', 'CancelRequested')
                ORDER BY CASE status
                    WHEN 'InProgress' THEN 0
                    WHEN 'CancelRequested' THEN 1
                    ELSE 2
                END, requested_at ASC, request_id ASC
                LIMIT 1
            )
              AND status IN ('Pending', 'InProgress', 'CancelRequested')
            RETURNING request_id;
            """;
        command.Parameters.AddWithValue("$completedAt", DateTimeOffset.UtcNow.ToString("O"));
        command.Parameters.AddWithValue("$errorMessage", cancellationMessage);
        return await command.ExecuteScalarAsync(cancellationToken) is not null;
    }

    public async Task<bool> IsCancellationRequestedAsync(string requestId, CancellationToken cancellationToken)
    {
        var databasePath = pathResolver.ResolveConfiguredPath() ?? pathResolver.Resolve();
        if (string.IsNullOrWhiteSpace(databasePath))
        {
            return false;
        }

        try
        {
            await using var connection = SqliteConnections.Open(databasePath);
            await connection.OpenAsync(cancellationToken);
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT status FROM run_queue WHERE request_id = $requestId LIMIT 1;";
            command.Parameters.AddWithValue("$requestId", requestId);
            var result = await command.ExecuteScalarAsync(cancellationToken);
            return result is string status &&
                   string.Equals(status, "CancelRequested", StringComparison.OrdinalIgnoreCase);
        }
        catch (SqliteException ex) when (SqliteConnections.IsBusyOrLocked(ex))
        {
            return false;
        }
    }

    public async Task CompleteAsync(string requestId, string runId, CancellationToken cancellationToken)
    {
        await UpdateTerminalStatusAsync(requestId, "Completed", runId, null, cancellationToken);
    }

    public async Task CancelAsync(string requestId, string? runId, string? errorMessage, CancellationToken cancellationToken)
    {
        await UpdateTerminalStatusAsync(requestId, "Canceled", runId, errorMessage, cancellationToken);
    }

    public async Task FailAsync(string requestId, string? runId, string errorMessage, CancellationToken cancellationToken)
    {
        await UpdateTerminalStatusAsync(requestId, "Failed", runId, errorMessage, cancellationToken);
    }

    public async Task<int> RecoverOrphanedActiveRunsAsync(string? errorMessage, CancellationToken cancellationToken)
    {
        var databasePath = pathResolver.ResolveConfiguredPath() ?? pathResolver.Resolve();
        if (string.IsNullOrWhiteSpace(databasePath))
        {
            return 0;
        }

        await using var connection = SqliteConnections.Open(databasePath);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            UPDATE run_queue
            SET status = CASE status
                    WHEN 'CancelRequested' THEN 'Canceled'
                    ELSE 'Failed'
                END,
                completed_at = $completedAt,
                error_message = COALESCE(error_message, $errorMessage)
            WHERE status IN ('InProgress', 'CancelRequested');
            """;
        command.Parameters.AddWithValue("$completedAt", DateTimeOffset.UtcNow.ToString("O"));
        command.Parameters.AddWithValue("$errorMessage", (object?)errorMessage ?? DBNull.Value);
        return await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<RunQueueRequest> SeedRecoveryProbeAsync(RunQueueRecoveryProbeRequest request, CancellationToken cancellationToken)
    {
        if (!string.Equals(request.Status, "InProgress", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(request.Status, "CancelRequested", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Recovery probe status must be InProgress or CancelRequested.");
        }

        var databasePath = pathResolver.ResolveConfiguredPath() ?? pathResolver.Resolve();
        if (string.IsNullOrWhiteSpace(databasePath))
        {
            throw new InvalidOperationException("SQLite path could not be resolved.");
        }

        var now = DateTimeOffset.UtcNow;
        var startedAt = now.AddMinutes(-Math.Max(1, request.StartedMinutesAgo));
        var seeded = new RunQueueRequest(
            RequestId: string.IsNullOrWhiteSpace(request.RequestId)
                ? $"recovery-probe-{now:yyyyMMddHHmmssfff}"
                : request.RequestId,
            Mode: string.IsNullOrWhiteSpace(request.Mode) ? "BulkSync" : request.Mode,
            DryRun: request.DryRun,
            RunTrigger: string.IsNullOrWhiteSpace(request.RunTrigger) ? "AutomationRecoveryProbe" : request.RunTrigger,
            RequestedBy: request.RequestedBy,
            Status: request.Status,
            RequestedAt: startedAt,
            StartedAt: startedAt,
            CompletedAt: null,
            RunId: request.RunId,
            ErrorMessage: null);

        await using var connection = SqliteConnections.Open(databasePath);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT INTO run_queue (
              request_id,
              mode,
              dry_run,
              run_trigger,
              requested_by,
              status,
              requested_at,
              started_at,
              completed_at,
              run_id,
              worker_name,
              error_message
            )
            VALUES (
              $requestId,
              $mode,
              $dryRun,
              $runTrigger,
              $requestedBy,
              $status,
              $requestedAt,
              $startedAt,
              NULL,
              $runId,
              $workerName,
              NULL
            );
            """;
        Bind(command, seeded, request.WorkerName);
        command.Parameters.AddWithValue("$runId", (object?)seeded.RunId ?? DBNull.Value);
        try
        {
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        catch (SqliteException ex) when (ex.SqliteErrorCode == 19)
        {
            throw new RunQueueConflictException();
        }
        return seeded;
    }

    private async Task UpdateTerminalStatusAsync(string requestId, string status, string? runId, string? errorMessage, CancellationToken cancellationToken)
    {
        var databasePath = pathResolver.ResolveConfiguredPath() ?? pathResolver.Resolve();
        if (string.IsNullOrWhiteSpace(databasePath))
        {
            return;
        }

        await using var connection = SqliteConnections.Open(databasePath);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            UPDATE run_queue
            SET status = $status,
                completed_at = $completedAt,
                run_id = $runId,
                error_message = $errorMessage
            WHERE request_id = $requestId;
            """;
        command.Parameters.AddWithValue("$requestId", requestId);
        command.Parameters.AddWithValue("$status", status);
        command.Parameters.AddWithValue("$completedAt", DateTimeOffset.UtcNow.ToString("O"));
        command.Parameters.AddWithValue("$runId", (object?)runId ?? DBNull.Value);
        command.Parameters.AddWithValue("$errorMessage", (object?)errorMessage ?? DBNull.Value);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static void Bind(SqliteCommand command, RunQueueRequest request, string? workerName)
    {
        command.Parameters.AddWithValue("$requestId", request.RequestId);
        command.Parameters.AddWithValue("$mode", request.Mode);
        command.Parameters.AddWithValue("$dryRun", request.DryRun ? 1 : 0);
        command.Parameters.AddWithValue("$runTrigger", request.RunTrigger);
        command.Parameters.AddWithValue("$requestedBy", (object?)request.RequestedBy ?? DBNull.Value);
        command.Parameters.AddWithValue("$status", request.Status);
        command.Parameters.AddWithValue("$requestedAt", request.RequestedAt.ToString("O"));
        command.Parameters.AddWithValue("$startedAt", (object?)request.StartedAt?.ToString("O") ?? DBNull.Value);
        command.Parameters.AddWithValue("$workerName", (object?)workerName ?? DBNull.Value);
    }

    private static RunQueueRequest Map(SqliteDataReader reader)
    {
        return new RunQueueRequest(
            RequestId: reader.GetStringOrDefault("request_id") ?? string.Empty,
            Mode: reader.GetStringOrDefault("mode") ?? "BulkSync",
            DryRun: reader.GetInt32OrDefault("dry_run") != 0,
            RunTrigger: reader.GetStringOrDefault("run_trigger") ?? "AdHoc",
            RequestedBy: reader.GetStringOrDefault("requested_by"),
            Status: reader.GetStringOrDefault("status") ?? "Pending",
            RequestedAt: DateTimeOffset.Parse(reader.GetStringOrDefault("requested_at") ?? DateTimeOffset.UtcNow.ToString("O")),
            StartedAt: ParseDate(reader.GetStringOrDefault("started_at")),
            CompletedAt: ParseDate(reader.GetStringOrDefault("completed_at")),
            RunId: reader.GetStringOrDefault("run_id"),
            ErrorMessage: reader.GetStringOrDefault("error_message"));
    }

    private static DateTimeOffset? ParseDate(string? value)
    {
        return DateTimeOffset.TryParse(value, out var parsed) ? parsed : null;
    }
}

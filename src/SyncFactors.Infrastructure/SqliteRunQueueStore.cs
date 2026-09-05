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
            ErrorMessage: null,
            TargetWorkerId: request.TargetWorkerId);

        if (RunQueueProtocol.IsReservedDeletionMode(queued.Mode))
        {
            throw new ReservedDeletionModeRejectedException();
        }

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
              error_message,
              target_worker_id
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
              NULL,
              $targetWorkerId
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
                  AND mode IS NOT NULL
                  AND mode COLLATE NOCASE NOT IN ('DeleteAllUsers', 'GraveyardDeleteApproval', 'GraveyardAutoDelete')
                ORDER BY requested_at ASC, request_id ASC
                LIMIT 1
            )
              AND status = 'Pending'
              AND mode IS NOT NULL
              AND mode COLLATE NOCASE NOT IN ('DeleteAllUsers', 'GraveyardDeleteApproval', 'GraveyardAutoDelete')
              AND NOT EXISTS (
                SELECT 1
                FROM run_queue
                WHERE status IN ('InProgress', 'CancelRequested')
            )
            RETURNING request_id, mode, dry_run, run_trigger, requested_by, status, requested_at, started_at, completed_at, run_id, error_message, target_worker_id;
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

    public async Task QuarantineReservedAsync(string requestId, CancellationToken cancellationToken)
    {
        var databasePath = pathResolver.ResolveConfiguredPath() ?? pathResolver.Resolve();
        if (string.IsNullOrWhiteSpace(databasePath))
        {
            throw new ReservedDeletionModeRejectedException();
        }

        await using var connection = SqliteConnections.Open(databasePath);
        await connection.OpenAsync(cancellationToken);
        await using var begin = connection.CreateCommand();
        begin.CommandText = "BEGIN IMMEDIATE;";
        await begin.ExecuteNonQueryAsync(cancellationToken);
        try
        {
            string? mode;
            string originalStatus;
            bool dryRun;
            string? runId;
            await using (var read = connection.CreateCommand())
            {
                read.CommandText =
                    """
                    SELECT mode, status, dry_run, run_id
                    FROM run_queue
                    WHERE request_id = $requestId
                    LIMIT 1;
                    """;
                read.Parameters.AddWithValue("$requestId", requestId);
                await using var reader = await read.ExecuteReaderAsync(cancellationToken);
                if (!await reader.ReadAsync(cancellationToken))
                {
                    throw new ReservedDeletionModeRejectedException();
                }

                mode = reader.GetStringOrDefault("mode");
                originalStatus = reader.GetStringOrDefault("status") ?? string.Empty;
                dryRun = reader.GetInt32OrDefault("dry_run") != 0;
                runId = reader.GetStringOrDefault("run_id");
            }

            if (!RunQueueProtocol.IsReservedDeletionMode(mode))
            {
                throw new ReservedDeletionModeRejectedException();
            }

            var classification = originalStatus is "InProgress" or "CancelRequested"
                ? "OutcomeUnknown"
                : "CapabilityDisabledBeforeExecution";
            await using (var ledger = connection.CreateCommand())
            {
                ledger.CommandText =
                    """
                    INSERT INTO directory_deletion_quarantine (
                      source_kind, source_id, destructive_mode, original_status, classification,
                      reason_code, reason_message, run_id, dry_run, captured_at
                    ) VALUES (
                      'RunQueueRequest', $requestId, $mode, $originalStatus, $classification,
                      'AtomicIdentityFenceUnavailable', $reasonMessage, $runId, $dryRun, $capturedAt
                    ) ON CONFLICT(source_kind, source_id) DO NOTHING;
                    """;
                ledger.Parameters.AddWithValue("$requestId", requestId);
                ledger.Parameters.AddWithValue("$mode", mode);
                ledger.Parameters.AddWithValue("$originalStatus", originalStatus);
                ledger.Parameters.AddWithValue("$classification", classification);
                ledger.Parameters.AddWithValue("$reasonMessage", "Graveyard deletion is unavailable; records remain review-only until an atomic AD object-identity fence is approved.");
                ledger.Parameters.AddWithValue("$runId", (object?)runId ?? DBNull.Value);
                ledger.Parameters.AddWithValue("$dryRun", dryRun ? 1 : 0);
                ledger.Parameters.AddWithValue("$capturedAt", DateTimeOffset.UtcNow.ToString("O"));
                await ledger.ExecuteNonQueryAsync(cancellationToken);
            }

            await using (var quarantine = connection.CreateCommand())
            {
                quarantine.CommandText =
                    """
                    UPDATE run_queue
                    SET status = 'Quarantined',
                        completed_at = COALESCE(completed_at, $completedAt),
                        error_message = $reasonMessage
                    WHERE request_id = $requestId
                      AND mode COLLATE NOCASE IN ('DeleteAllUsers', 'GraveyardDeleteApproval', 'GraveyardAutoDelete');
                    """;
                quarantine.Parameters.AddWithValue("$requestId", requestId);
                quarantine.Parameters.AddWithValue("$completedAt", DateTimeOffset.UtcNow.ToString("O"));
                quarantine.Parameters.AddWithValue("$reasonMessage", "Graveyard deletion is unavailable; records remain review-only until an atomic AD object-identity fence is approved.");
                if (await quarantine.ExecuteNonQueryAsync(cancellationToken) != 1)
                {
                    throw new ReservedDeletionModeRejectedException();
                }
            }

            if (!string.IsNullOrWhiteSpace(runId))
            {
                await using (var blockRun = connection.CreateCommand())
                {
                    blockRun.CommandText =
                        """
                        UPDATE runs
                        SET status = 'Blocked',
                            completed_at = COALESCE(completed_at, $completedAt)
                        WHERE run_id = $runId
                          AND status IN ('Pending', 'InProgress', 'CancelRequested');
                        """;
                    blockRun.Parameters.AddWithValue("$runId", runId);
                    blockRun.Parameters.AddWithValue("$completedAt", DateTimeOffset.UtcNow.ToString("O"));
                    await blockRun.ExecuteNonQueryAsync(cancellationToken);
                }

                await using (var blockRuntime = connection.CreateCommand())
                {
                    blockRuntime.CommandText =
                        """
                        UPDATE runtime_status
                        SET status = 'Blocked',
                            stage = 'DeletionCapabilityDisabled',
                            completed_at = COALESCE(completed_at, $completedAt),
                            current_worker_id = NULL,
                            last_action = $reasonMessage,
                            error_message = $reasonMessage,
                            snapshot_json = json_set(
                                snapshot_json,
                                '$.Status', 'Blocked',
                                '$.Stage', 'DeletionCapabilityDisabled',
                                '$.CompletedAt', $completedAt,
                                '$.CurrentWorkerId', NULL,
                                '$.LastAction', $reasonMessage,
                                '$.ErrorMessage', $reasonMessage)
                        WHERE run_id = $runId
                          AND status IN ('Pending', 'InProgress', 'CancelRequested');
                        """;
                    blockRuntime.Parameters.AddWithValue("$runId", runId);
                    blockRuntime.Parameters.AddWithValue("$completedAt", DateTimeOffset.UtcNow.ToString("O"));
                    blockRuntime.Parameters.AddWithValue("$reasonMessage", "Graveyard deletion is unavailable; records remain review-only until an atomic AD object-identity fence is approved.");
                    await blockRuntime.ExecuteNonQueryAsync(cancellationToken);
                }
            }

            await using var commit = connection.CreateCommand();
            commit.CommandText = "COMMIT;";
            await commit.ExecuteNonQueryAsync(cancellationToken);
        }
        catch
        {
            await using var rollback = connection.CreateCommand();
            rollback.CommandText = "ROLLBACK;";
            try
            {
                await rollback.ExecuteNonQueryAsync(CancellationToken.None);
            }
            catch (SqliteException)
            {
            }

            throw;
        }
    }

    public async Task<int> QuarantineReservedModesAsync(CancellationToken cancellationToken)
    {
        var databasePath = pathResolver.ResolveConfiguredPath() ?? pathResolver.Resolve();
        if (string.IsNullOrWhiteSpace(databasePath))
        {
            throw new ReservedDeletionModeRejectedException();
        }

        var requestIds = new List<string>();
        await using (var connection = SqliteConnections.Open(databasePath))
        {
            await connection.OpenAsync(cancellationToken);
            await using var command = connection.CreateCommand();
            command.CommandText =
                """
                SELECT request_id
                FROM run_queue
                WHERE status IN ('Pending', 'InProgress', 'CancelRequested')
                  AND mode COLLATE NOCASE IN ('DeleteAllUsers', 'GraveyardDeleteApproval', 'GraveyardAutoDelete')
                ORDER BY requested_at ASC, request_id ASC;
                """;
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                requestIds.Add(reader.GetString(0));
            }
        }

        foreach (var requestId in requestIds)
        {
            await QuarantineReservedAsync(requestId, cancellationToken);
        }

        return requestIds.Count;
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
            SELECT request_id, mode, dry_run, run_trigger, requested_by, status, requested_at, started_at, completed_at, run_id, error_message, target_worker_id
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
            SELECT request_id, mode, dry_run, run_trigger, requested_by, status, requested_at, started_at, completed_at, run_id, error_message, target_worker_id
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
                  AND mode IS NOT NULL
                  AND mode COLLATE NOCASE NOT IN ('DeleteAllUsers', 'GraveyardDeleteApproval', 'GraveyardAutoDelete')
                ORDER BY CASE status
                    WHEN 'InProgress' THEN 0
                    WHEN 'CancelRequested' THEN 1
                    ELSE 2
                END, requested_at ASC, request_id ASC
                LIMIT 1
            )
              AND status IN ('Pending', 'InProgress', 'CancelRequested')
              AND mode IS NOT NULL
              AND mode COLLATE NOCASE NOT IN ('DeleteAllUsers', 'GraveyardDeleteApproval', 'GraveyardAutoDelete')
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
            WHERE status IN ('InProgress', 'CancelRequested')
              AND mode IS NOT NULL
              AND mode COLLATE NOCASE NOT IN ('DeleteAllUsers', 'GraveyardDeleteApproval', 'GraveyardAutoDelete');
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
              error_message,
              target_worker_id
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
              NULL,
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
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.Transaction = (SqliteTransaction)transaction;
        command.CommandText =
            """
            UPDATE run_queue
            SET status = $status,
                completed_at = $completedAt,
                run_id = $runId,
                error_message = $errorMessage
            WHERE request_id = $requestId
              AND status IN ('InProgress', 'CancelRequested')
              AND mode IS NOT NULL
              AND mode COLLATE NOCASE NOT IN ('DeleteAllUsers', 'GraveyardDeleteApproval', 'GraveyardAutoDelete');
            """;
        command.Parameters.AddWithValue("$requestId", requestId);
        command.Parameters.AddWithValue("$status", status);
        command.Parameters.AddWithValue("$completedAt", DateTimeOffset.UtcNow.ToString("O"));
        command.Parameters.AddWithValue("$runId", (object?)runId ?? DBNull.Value);
        command.Parameters.AddWithValue("$errorMessage", (object?)errorMessage ?? DBNull.Value);
        var updated = await command.ExecuteNonQueryAsync(cancellationToken);
        if (updated == 0)
        {
            await using var classification = connection.CreateCommand();
            classification.Transaction = (SqliteTransaction)transaction;
            classification.CommandText = "SELECT mode FROM run_queue WHERE request_id = $requestId LIMIT 1;";
            classification.Parameters.AddWithValue("$requestId", requestId);
            var persistedMode = await classification.ExecuteScalarAsync(cancellationToken) as string;
            if (RunQueueProtocol.IsReservedDeletionMode(persistedMode))
            {
                throw new ReservedDeletionModeRejectedException();
            }
        }

        await transaction.CommitAsync(cancellationToken);
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
        command.Parameters.AddWithValue("$targetWorkerId", (object?)request.TargetWorkerId ?? DBNull.Value);
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
            ErrorMessage: reader.GetStringOrDefault("error_message"),
            TargetWorkerId: reader.GetStringOrDefault("target_worker_id"));
    }

    private static DateTimeOffset? ParseDate(string? value)
    {
        return DateTimeOffset.TryParse(value, out var parsed) ? parsed : null;
    }
}

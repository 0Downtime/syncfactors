using Microsoft.Data.Sqlite;
using SyncFactors.Contracts;
using SyncFactors.Domain;

namespace SyncFactors.Infrastructure;

public sealed class SqliteRunQueueStore(SqlitePathResolver pathResolver) : IRunQueueStore
{
    public async Task<RunQueueRequest> EnqueueAsync(StartRunRequest request, CancellationToken cancellationToken)
    {
        var databasePath = pathResolver.ResolveConfiguredPath() ?? pathResolver.Resolve();
        if (string.IsNullOrWhiteSpace(databasePath))
        {
            throw new InvalidOperationException("SQLite path could not be resolved.");
        }

        var queued = new RunQueueRequest(
            RequestId: $"runreq-{DateTimeOffset.UtcNow:yyyyMMddHHmmssfff}",
            Mode: "BulkSync",
            DryRun: request.DryRun,
            RunTrigger: string.IsNullOrWhiteSpace(request.RunTrigger) ? "AdHoc" : request.RunTrigger,
            RequestedBy: request.RequestedBy,
            Status: "Pending",
            RequestedAt: DateTimeOffset.UtcNow,
            StartedAt: null,
            CompletedAt: null,
            RunId: null,
            ErrorMessage: null);

        await using var connection = new SqliteConnection($"Data Source={databasePath}");
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
        await command.ExecuteNonQueryAsync(cancellationToken);
        return queued;
    }

    public async Task<RunQueueRequest?> ClaimNextPendingAsync(string workerName, CancellationToken cancellationToken)
    {
        var databasePath = pathResolver.ResolveConfiguredPath() ?? pathResolver.Resolve();
        if (string.IsNullOrWhiteSpace(databasePath))
        {
            return null;
        }

        await using var connection = new SqliteConnection($"Data Source={databasePath}");
        await connection.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        await using (var activeCommand = connection.CreateCommand())
        {
            activeCommand.Transaction = (SqliteTransaction)transaction;
            activeCommand.CommandText = "SELECT request_id FROM run_queue WHERE status = 'InProgress' LIMIT 1;";
            var active = await activeCommand.ExecuteScalarAsync(cancellationToken);
            if (active is not null)
            {
                await transaction.CommitAsync(cancellationToken);
                return null;
            }
        }

        RunQueueRequest? pending = null;
        await using (var selectCommand = connection.CreateCommand())
        {
            selectCommand.Transaction = (SqliteTransaction)transaction;
            selectCommand.CommandText =
                """
                SELECT request_id, mode, dry_run, run_trigger, requested_by, status, requested_at, started_at, completed_at, run_id, error_message
                FROM run_queue
                WHERE status = 'Pending'
                ORDER BY requested_at ASC
                LIMIT 1;
                """;
            await using var reader = await selectCommand.ExecuteReaderAsync(cancellationToken);
            if (await reader.ReadAsync(cancellationToken))
            {
                pending = Map(reader);
            }
        }

        if (pending is null)
        {
            await transaction.CommitAsync(cancellationToken);
            return null;
        }

        var claimed = pending with
        {
            Status = "InProgress",
            StartedAt = DateTimeOffset.UtcNow
        };

        await using (var updateCommand = connection.CreateCommand())
        {
            updateCommand.Transaction = (SqliteTransaction)transaction;
            updateCommand.CommandText =
                """
                UPDATE run_queue
                SET status = $status,
                    started_at = $startedAt,
                    worker_name = $workerName
                WHERE request_id = $requestId;
                """;
            Bind(updateCommand, claimed, workerName);
            await updateCommand.ExecuteNonQueryAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
        return claimed;
    }

    public async Task<bool> HasPendingOrActiveRunAsync(CancellationToken cancellationToken)
    {
        var databasePath = pathResolver.ResolveConfiguredPath() ?? pathResolver.Resolve();
        if (string.IsNullOrWhiteSpace(databasePath))
        {
            return false;
        }

        await using var connection = new SqliteConnection($"Data Source={databasePath}");
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT 1 FROM run_queue WHERE status IN ('Pending', 'InProgress') LIMIT 1;";
        var result = await command.ExecuteScalarAsync(cancellationToken);
        return result is not null;
    }

    public async Task CompleteAsync(string requestId, string runId, CancellationToken cancellationToken)
    {
        await UpdateTerminalStatusAsync(requestId, "Completed", runId, null, cancellationToken);
    }

    public async Task FailAsync(string requestId, string? runId, string errorMessage, CancellationToken cancellationToken)
    {
        await UpdateTerminalStatusAsync(requestId, "Failed", runId, errorMessage, cancellationToken);
    }

    private async Task UpdateTerminalStatusAsync(string requestId, string status, string? runId, string? errorMessage, CancellationToken cancellationToken)
    {
        var databasePath = pathResolver.ResolveConfiguredPath() ?? pathResolver.Resolve();
        if (string.IsNullOrWhiteSpace(databasePath))
        {
            return;
        }

        await using var connection = new SqliteConnection($"Data Source={databasePath}");
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

using Microsoft.Data.Sqlite;
using System.Text.Json;
using SyncFactors.Contracts;
using SyncFactors.Domain;

namespace SyncFactors.Infrastructure;

public sealed class SqliteRunRepository(SqlitePathResolver pathResolver) : IRunRepository
{
    private static readonly string[] BucketOrder =
    [
        "quarantined",
        "conflicts",
        "manualReview",
        "guardrailFailures",
        "creates",
        "updates",
        "enables",
        "disables",
        "graveyardMoves",
        "deletions",
        "unchanged",
    ];

    private static readonly IReadOnlyDictionary<string, string> BucketLabels = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["creates"] = "Creates",
        ["updates"] = "Updates",
        ["enables"] = "Enables",
        ["disables"] = "Disables",
        ["graveyardMoves"] = "Graveyard Moves",
        ["deletions"] = "Deletions",
        ["quarantined"] = "Quarantined",
        ["conflicts"] = "Conflicts",
        ["guardrailFailures"] = "Guardrails",
        ["manualReview"] = "Manual Review",
        ["unchanged"] = "Unchanged",
    };

    public async Task<IReadOnlyList<RunSummary>> ListRunsAsync(CancellationToken cancellationToken)
    {
        var databasePath = pathResolver.Resolve();
        if (string.IsNullOrWhiteSpace(databasePath))
        {
            return [];
        }

        await using var connection = OpenConnection(databasePath);
        await connection.OpenAsync(cancellationToken);

        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            WITH recent_runs AS (
              SELECT
                run_id,
                path,
                artifact_type,
                config_path,
                mapping_config_path,
                mode,
                dry_run,
                run_trigger,
                requested_by,
                status,
                started_at,
                completed_at,
                duration_seconds,
                creates,
                updates,
                enables,
                disables,
                graveyard_moves,
                deletions,
                quarantined,
                conflicts,
                guardrail_failures,
                manual_review,
                unchanged,
                report_json
              FROM runs
              WHERE artifact_type IS NULL OR artifact_type <> 'WorkerPreview'
              ORDER BY COALESCE(started_at, '') DESC, COALESCE(path, '') DESC
              LIMIT 25
            ),
            entry_counts AS (
              SELECT
                run_id,
                COUNT(1) AS entry_count,
                SUM(CASE WHEN bucket = 'creates' THEN 1 ELSE 0 END) AS entry_creates,
                SUM(CASE WHEN bucket = 'updates' THEN 1 ELSE 0 END) AS entry_updates,
                SUM(CASE WHEN bucket = 'enables' THEN 1 ELSE 0 END) AS entry_enables,
                SUM(CASE WHEN bucket = 'disables' THEN 1 ELSE 0 END) AS entry_disables,
                SUM(CASE WHEN bucket = 'graveyardMoves' THEN 1 ELSE 0 END) AS entry_graveyard_moves,
                SUM(CASE WHEN bucket = 'deletions' THEN 1 ELSE 0 END) AS entry_deletions,
                SUM(CASE WHEN bucket = 'quarantined' THEN 1 ELSE 0 END) AS entry_quarantined,
                SUM(CASE WHEN bucket = 'conflicts' THEN 1 ELSE 0 END) AS entry_conflicts,
                SUM(CASE WHEN bucket = 'guardrailFailures' THEN 1 ELSE 0 END) AS entry_guardrail_failures,
                SUM(CASE WHEN bucket = 'manualReview' THEN 1 ELSE 0 END) AS entry_manual_review,
                SUM(CASE WHEN bucket = 'unchanged' THEN 1 ELSE 0 END) AS entry_unchanged
              FROM run_entries
              WHERE run_id IN (SELECT run_id FROM recent_runs)
              GROUP BY run_id
            )
            SELECT
              recent_runs.run_id,
              recent_runs.path,
              recent_runs.artifact_type,
              recent_runs.config_path,
              recent_runs.mapping_config_path,
              recent_runs.mode,
              recent_runs.dry_run,
              recent_runs.run_trigger,
              recent_runs.requested_by,
              recent_runs.status,
              recent_runs.started_at,
              recent_runs.completed_at,
              recent_runs.duration_seconds,
              recent_runs.creates,
              recent_runs.updates,
              recent_runs.enables,
              recent_runs.disables,
              recent_runs.graveyard_moves,
              recent_runs.deletions,
              recent_runs.quarantined,
              recent_runs.conflicts,
              recent_runs.guardrail_failures,
              recent_runs.manual_review,
              recent_runs.unchanged,
              recent_runs.report_json,
              COALESCE(entry_counts.entry_count, 0) AS entry_count,
              COALESCE(entry_counts.entry_creates, 0) AS entry_creates,
              COALESCE(entry_counts.entry_updates, 0) AS entry_updates,
              COALESCE(entry_counts.entry_enables, 0) AS entry_enables,
              COALESCE(entry_counts.entry_disables, 0) AS entry_disables,
              COALESCE(entry_counts.entry_graveyard_moves, 0) AS entry_graveyard_moves,
              COALESCE(entry_counts.entry_deletions, 0) AS entry_deletions,
              COALESCE(entry_counts.entry_quarantined, 0) AS entry_quarantined,
              COALESCE(entry_counts.entry_conflicts, 0) AS entry_conflicts,
              COALESCE(entry_counts.entry_guardrail_failures, 0) AS entry_guardrail_failures,
              COALESCE(entry_counts.entry_manual_review, 0) AS entry_manual_review,
              COALESCE(entry_counts.entry_unchanged, 0) AS entry_unchanged
            FROM recent_runs
            LEFT JOIN entry_counts ON entry_counts.run_id = recent_runs.run_id
            ORDER BY COALESCE(recent_runs.started_at, '') DESC, COALESCE(recent_runs.path, '') DESC;
            """;

        var runs = new List<RunSummary>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var row = MapRunRow(reader);
            if (string.IsNullOrWhiteSpace(row.RunId))
            {
                continue;
            }

            runs.Add(new RunSummary(
                RunId: row.RunId!,
                Path: row.Path,
                ArtifactType: row.ArtifactType ?? "SyncReport",
                ConfigPath: row.ConfigPath,
                MappingConfigPath: row.MappingConfigPath,
                Mode: row.Mode ?? "Unknown",
                DryRun: row.DryRun != 0,
                RunTrigger: row.RunTrigger ?? "AdHoc",
                RequestedBy: row.RequestedBy,
                Status: row.Status ?? "Unknown",
                StartedAt: ParseDate(row.StartedAt) ?? DateTimeOffset.MinValue,
                CompletedAt: ParseDate(row.CompletedAt),
                DurationSeconds: row.DurationSeconds,
                ProcessedWorkers: Sum(row.Creates, row.Updates, row.Enables, row.Disables, row.GraveyardMoves, row.Deletions, row.Unchanged),
                TotalWorkers: Sum(row.Creates, row.Updates, row.Enables, row.Disables, row.GraveyardMoves, row.Deletions, row.Quarantined, row.Conflicts, row.GuardrailFailures, row.ManualReview, row.Unchanged),
                Creates: row.Creates,
                Updates: row.Updates,
                Enables: row.Enables,
                Disables: row.Disables,
                GraveyardMoves: row.GraveyardMoves,
                Deletions: row.Deletions,
                Quarantined: row.Quarantined,
                Conflicts: row.Conflicts,
                GuardrailFailures: row.GuardrailFailures,
                ManualReview: row.ManualReview,
                Unchanged: row.Unchanged,
                SyncScope: ResolveSyncScope(row.Mode, row.ArtifactType, row.ReportJson)));
        }

        return runs;
    }

    public async Task<RunDetail?> GetRunAsync(string runId, CancellationToken cancellationToken)
    {
        var databasePath = pathResolver.Resolve();
        if (string.IsNullOrWhiteSpace(databasePath))
        {
            return null;
        }

        await using var connection = OpenConnection(databasePath);
        await connection.OpenAsync(cancellationToken);

        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT
              runs.run_id,
              runs.path,
              runs.artifact_type,
              runs.config_path,
              runs.mapping_config_path,
              runs.mode,
              runs.dry_run,
              runs.run_trigger,
              runs.requested_by,
              runs.status,
              runs.started_at,
              runs.completed_at,
              runs.duration_seconds,
              runs.creates,
              runs.updates,
              runs.enables,
              runs.disables,
              runs.graveyard_moves,
              runs.deletions,
              runs.quarantined,
              runs.conflicts,
              runs.guardrail_failures,
              runs.manual_review,
              runs.unchanged,
              runs.report_json,
              COALESCE(entry_counts.entry_count, 0) AS entry_count,
              COALESCE(entry_counts.entry_creates, 0) AS entry_creates,
              COALESCE(entry_counts.entry_updates, 0) AS entry_updates,
              COALESCE(entry_counts.entry_enables, 0) AS entry_enables,
              COALESCE(entry_counts.entry_disables, 0) AS entry_disables,
              COALESCE(entry_counts.entry_graveyard_moves, 0) AS entry_graveyard_moves,
              COALESCE(entry_counts.entry_deletions, 0) AS entry_deletions,
              COALESCE(entry_counts.entry_quarantined, 0) AS entry_quarantined,
              COALESCE(entry_counts.entry_conflicts, 0) AS entry_conflicts,
              COALESCE(entry_counts.entry_guardrail_failures, 0) AS entry_guardrail_failures,
              COALESCE(entry_counts.entry_manual_review, 0) AS entry_manual_review,
              COALESCE(entry_counts.entry_unchanged, 0) AS entry_unchanged
            FROM runs
            LEFT JOIN (
              SELECT
                run_id,
                COUNT(1) AS entry_count,
                SUM(CASE WHEN bucket = 'creates' THEN 1 ELSE 0 END) AS entry_creates,
                SUM(CASE WHEN bucket = 'updates' THEN 1 ELSE 0 END) AS entry_updates,
                SUM(CASE WHEN bucket = 'enables' THEN 1 ELSE 0 END) AS entry_enables,
                SUM(CASE WHEN bucket = 'disables' THEN 1 ELSE 0 END) AS entry_disables,
                SUM(CASE WHEN bucket = 'graveyardMoves' THEN 1 ELSE 0 END) AS entry_graveyard_moves,
                SUM(CASE WHEN bucket = 'deletions' THEN 1 ELSE 0 END) AS entry_deletions,
                SUM(CASE WHEN bucket = 'quarantined' THEN 1 ELSE 0 END) AS entry_quarantined,
                SUM(CASE WHEN bucket = 'conflicts' THEN 1 ELSE 0 END) AS entry_conflicts,
                SUM(CASE WHEN bucket = 'guardrailFailures' THEN 1 ELSE 0 END) AS entry_guardrail_failures,
                SUM(CASE WHEN bucket = 'manualReview' THEN 1 ELSE 0 END) AS entry_manual_review,
                SUM(CASE WHEN bucket = 'unchanged' THEN 1 ELSE 0 END) AS entry_unchanged
              FROM run_entries
              WHERE run_id = $runId
              GROUP BY run_id
            ) AS entry_counts ON entry_counts.run_id = runs.run_id
            WHERE runs.run_id = $runId
            LIMIT 1;
            """;
        command.Parameters.AddWithValue("$runId", runId);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        var row = MapRunWithReportRow(reader);
        if (string.IsNullOrWhiteSpace(row.RunId) || string.IsNullOrWhiteSpace(row.ReportJson))
        {
            return null;
        }

        var run = MapRunSummary(row);
        return new RunDetail(
            Run: run,
            Report: ParseJson(row.ReportJson),
            BucketCounts: BuildBucketCounts(run));
    }

    public async Task<WorkerPreviewResult?> GetWorkerPreviewAsync(string runId, CancellationToken cancellationToken)
    {
        var run = await GetRunAsync(runId, cancellationToken);
        if (run is null || !string.Equals(run.Run.ArtifactType, "WorkerPreview", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        if (run.Report.ValueKind != JsonValueKind.Object || !run.Report.TryGetProperty("preview", out var previewJson))
        {
            return null;
        }

        return JsonSerializer.Deserialize<WorkerPreviewResult>(previewJson.GetRawText());
    }

    public async Task<IReadOnlyList<WorkerPreviewHistoryItem>> ListWorkerPreviewHistoryAsync(string workerId, int take, CancellationToken cancellationToken)
    {
        var databasePath = pathResolver.Resolve();
        if (string.IsNullOrWhiteSpace(databasePath))
        {
            return [];
        }

        await using var connection = OpenConnection(databasePath);
        await connection.OpenAsync(cancellationToken);

        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT
              r.run_id,
              r.status,
              r.started_at,
              r.report_json,
              e.worker_id,
              e.sam_account_name,
              e.bucket,
              e.reason,
              e.item_json
            FROM runs r
            JOIN run_entries e ON e.run_id = r.run_id
            WHERE r.artifact_type = 'WorkerPreview'
              AND e.worker_id = $workerId
            ORDER BY COALESCE(r.started_at, '') DESC
            LIMIT $take;
            """;
        command.Parameters.AddWithValue("$workerId", workerId);
        command.Parameters.AddWithValue("$take", Math.Max(1, take));

        var history = new List<WorkerPreviewHistoryItem>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var report = reader.GetStringOrDefault("report_json");
            var item = reader.GetStringOrDefault("item_json");
            var preview = !string.IsNullOrWhiteSpace(report)
                ? JsonSerializer.Deserialize<WorkerPreviewResult>(GetProperty(ParseJson(report), "preview")?.GetRawText() ?? "null")
                : null;
            var entryItem = string.IsNullOrWhiteSpace(item) ? EmptyObject() : ParseJson(item);
            var diffRows = GetDiffRows(entryItem, null, reader.GetStringOrDefault("worker_id"), reader.GetStringOrDefault("bucket") ?? "updates");
            history.Add(new WorkerPreviewHistoryItem(
                RunId: reader.GetStringOrDefault("run_id") ?? string.Empty,
                WorkerId: reader.GetStringOrDefault("worker_id") ?? workerId,
                SamAccountName: reader.GetStringOrDefault("sam_account_name"),
                Bucket: reader.GetStringOrDefault("bucket") ?? "updates",
                Status: reader.GetStringOrDefault("status"),
                StartedAt: ParseDate(reader.GetStringOrDefault("started_at")) ?? DateTimeOffset.MinValue,
                ChangeCount: diffRows.Count(row => row.Changed),
                Action: preview?.OperationSummary?.Action,
                Reason: reader.GetStringOrDefault("reason"),
                Fingerprint: preview?.Fingerprint ?? string.Empty));
        }

        return history;
    }

    public async Task SaveRunAsync(RunRecord run, CancellationToken cancellationToken)
    {
        var databasePath = pathResolver.ResolveConfiguredPath() ?? pathResolver.Resolve();
        if (string.IsNullOrWhiteSpace(databasePath))
        {
            return;
        }

        await using var connection = OpenWriteConnection(databasePath);
        await connection.OpenAsync(cancellationToken);

        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT INTO runs (
              run_id,
              path,
              artifact_type,
              config_path,
              mapping_config_path,
              mode,
              dry_run,
              run_trigger,
              requested_by,
              status,
              started_at,
              completed_at,
              duration_seconds,
              creates,
              updates,
              enables,
              disables,
              graveyard_moves,
              deletions,
              quarantined,
              conflicts,
              guardrail_failures,
              manual_review,
              unchanged,
              report_json
            )
            VALUES (
              $runId,
              $path,
              $artifactType,
              $configPath,
              $mappingConfigPath,
              $mode,
              $dryRun,
              $runTrigger,
              $requestedBy,
              $status,
              $startedAt,
              $completedAt,
              $durationSeconds,
              $creates,
              $updates,
              $enables,
              $disables,
              $graveyardMoves,
              $deletions,
              $quarantined,
              $conflicts,
              $guardrailFailures,
              $manualReview,
              $unchanged,
              $reportJson
            )
            ON CONFLICT(run_id) DO UPDATE SET
              path = excluded.path,
              artifact_type = excluded.artifact_type,
              config_path = excluded.config_path,
              mapping_config_path = excluded.mapping_config_path,
              mode = excluded.mode,
              dry_run = excluded.dry_run,
              run_trigger = excluded.run_trigger,
              requested_by = excluded.requested_by,
              status = excluded.status,
              started_at = excluded.started_at,
              completed_at = excluded.completed_at,
              duration_seconds = excluded.duration_seconds,
              creates = excluded.creates,
              updates = excluded.updates,
              enables = excluded.enables,
              disables = excluded.disables,
              graveyard_moves = excluded.graveyard_moves,
              deletions = excluded.deletions,
              quarantined = excluded.quarantined,
              conflicts = excluded.conflicts,
              guardrail_failures = excluded.guardrail_failures,
              manual_review = excluded.manual_review,
              unchanged = excluded.unchanged,
              report_json = excluded.report_json;
            """;
        BindRunRecord(command, run);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task ReplaceRunEntriesAsync(string runId, IReadOnlyList<RunEntryRecord> entries, CancellationToken cancellationToken)
    {
        var databasePath = pathResolver.ResolveConfiguredPath() ?? pathResolver.Resolve();
        if (string.IsNullOrWhiteSpace(databasePath))
        {
            return;
        }

        await using var connection = OpenWriteConnection(databasePath);
        await connection.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        await using (var deleteCommand = connection.CreateCommand())
        {
            deleteCommand.Transaction = (SqliteTransaction)transaction;
            deleteCommand.CommandText = "DELETE FROM run_entries WHERE run_id = $runId;";
            deleteCommand.Parameters.AddWithValue("$runId", runId);
            await deleteCommand.ExecuteNonQueryAsync(cancellationToken);
        }

        foreach (var entry in entries)
        {
            await using var insertCommand = connection.CreateCommand();
            insertCommand.Transaction = (SqliteTransaction)transaction;
            insertCommand.CommandText =
                """
                INSERT INTO run_entries (
                  entry_id,
                  run_id,
                  bucket,
                  bucket_index,
                  worker_id,
                  sam_account_name,
                  reason,
                  review_category,
                  review_case_type,
                  started_at,
                  item_json
                )
                VALUES (
                  $entryId,
                  $runId,
                  $bucket,
                  $bucketIndex,
                  $workerId,
                  $samAccountName,
                  $reason,
                  $reviewCategory,
                  $reviewCaseType,
                  $startedAt,
                  $itemJson
                );
                """;
            insertCommand.Parameters.AddWithValue("$entryId", entry.EntryId);
            insertCommand.Parameters.AddWithValue("$runId", entry.RunId);
            insertCommand.Parameters.AddWithValue("$bucket", entry.Bucket);
            insertCommand.Parameters.AddWithValue("$bucketIndex", entry.BucketIndex);
            insertCommand.Parameters.AddWithValue("$workerId", (object?)entry.WorkerId ?? DBNull.Value);
            insertCommand.Parameters.AddWithValue("$samAccountName", (object?)entry.SamAccountName ?? DBNull.Value);
            insertCommand.Parameters.AddWithValue("$reason", (object?)entry.Reason ?? DBNull.Value);
            insertCommand.Parameters.AddWithValue("$reviewCategory", (object?)entry.ReviewCategory ?? DBNull.Value);
            insertCommand.Parameters.AddWithValue("$reviewCaseType", (object?)entry.ReviewCaseType ?? DBNull.Value);
            insertCommand.Parameters.AddWithValue("$startedAt", ToDbValue(entry.StartedAt));
            insertCommand.Parameters.AddWithValue("$itemJson", entry.Item.GetRawText());
            await insertCommand.ExecuteNonQueryAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
    }

    public async Task AppendRunEntryAsync(RunEntryRecord entry, CancellationToken cancellationToken)
    {
        var databasePath = pathResolver.ResolveConfiguredPath() ?? pathResolver.Resolve();
        if (string.IsNullOrWhiteSpace(databasePath))
        {
            return;
        }

        await using var connection = OpenWriteConnection(databasePath);
        await connection.OpenAsync(cancellationToken);
        await using var insertCommand = connection.CreateCommand();
        insertCommand.CommandText =
            """
            INSERT INTO run_entries (
              entry_id,
              run_id,
              bucket,
              bucket_index,
              worker_id,
              sam_account_name,
              reason,
              review_category,
              review_case_type,
              started_at,
              item_json
            )
            VALUES (
              $entryId,
              $runId,
              $bucket,
              $bucketIndex,
              $workerId,
              $samAccountName,
              $reason,
              $reviewCategory,
              $reviewCaseType,
              $startedAt,
              $itemJson
            )
            ON CONFLICT(entry_id) DO UPDATE SET
              run_id = excluded.run_id,
              bucket = excluded.bucket,
              bucket_index = excluded.bucket_index,
              worker_id = excluded.worker_id,
              sam_account_name = excluded.sam_account_name,
              reason = excluded.reason,
              review_category = excluded.review_category,
              review_case_type = excluded.review_case_type,
              started_at = excluded.started_at,
              item_json = excluded.item_json;
            """;
        insertCommand.Parameters.AddWithValue("$entryId", entry.EntryId);
        insertCommand.Parameters.AddWithValue("$runId", entry.RunId);
        insertCommand.Parameters.AddWithValue("$bucket", entry.Bucket);
        insertCommand.Parameters.AddWithValue("$bucketIndex", entry.BucketIndex);
        insertCommand.Parameters.AddWithValue("$workerId", (object?)entry.WorkerId ?? DBNull.Value);
        insertCommand.Parameters.AddWithValue("$samAccountName", (object?)entry.SamAccountName ?? DBNull.Value);
        insertCommand.Parameters.AddWithValue("$reason", (object?)entry.Reason ?? DBNull.Value);
        insertCommand.Parameters.AddWithValue("$reviewCategory", (object?)entry.ReviewCategory ?? DBNull.Value);
        insertCommand.Parameters.AddWithValue("$reviewCaseType", (object?)entry.ReviewCaseType ?? DBNull.Value);
        insertCommand.Parameters.AddWithValue("$startedAt", ToDbValue(entry.StartedAt));
        insertCommand.Parameters.AddWithValue("$itemJson", entry.Item.GetRawText());
        await insertCommand.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<RunEntry>> GetRunEntriesAsync(
        string runId,
        string? bucket,
        string? workerId,
        string? reason,
        string? filter,
        string? entryId,
        int skip,
        int take,
        CancellationToken cancellationToken)
    {
        var databasePath = pathResolver.Resolve();
        if (string.IsNullOrWhiteSpace(databasePath))
        {
            return [];
        }

        await using var connection = OpenConnection(databasePath);
        await connection.OpenAsync(cancellationToken);

        await using var command = connection.CreateCommand();
        AddRunEntryFilterParameters(command, runId, bucket, workerId, reason, filter, entryId);
        command.Parameters.AddWithValue("$skip", Math.Max(0, skip));
        command.Parameters.AddWithValue("$take", Math.Max(1, take));

        command.CommandText =
            """
            SELECT
              e.entry_id,
              e.bucket,
              e.bucket_index,
              e.worker_id,
              e.sam_account_name,
              e.reason,
              e.review_category,
              e.review_case_type,
              e.started_at,
              e.item_json,
              r.run_id,
              r.artifact_type,
              r.mode,
              r.report_json
            FROM run_entries e
            JOIN runs r ON r.run_id = e.run_id
            WHERE e.run_id = $runId
              AND ($bucket IS NULL OR e.bucket = $bucket)
              AND (
                $workerId IS NULL
                OR LOWER(COALESCE(e.worker_id, '')) LIKE $workerId ESCAPE '\'
                OR LOWER(COALESCE(e.sam_account_name, '')) LIKE $workerId ESCAPE '\'
              )
              AND ($reason IS NULL OR LOWER(COALESCE(e.reason, '')) = LOWER($reason))
              AND ($entryId IS NULL OR e.entry_id = $entryId)
              AND (
                $filter IS NULL
                OR LOWER(COALESCE(e.item_json, '')) LIKE $filter ESCAPE '\'
                OR LOWER(COALESCE(e.reason, '')) LIKE $filter ESCAPE '\'
                OR LOWER(COALESCE(e.review_case_type, '')) LIKE $filter ESCAPE '\'
              )
            ORDER BY e.bucket ASC, e.bucket_index ASC, e.entry_id ASC
            LIMIT $take OFFSET $skip;
            """;

        var entries = new List<RunEntry>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            entries.Add(MapEntry(MapEntryRow(reader)));
        }

        return entries;
    }

    public async Task<int> CountRunEntriesAsync(
        string runId,
        string? bucket,
        string? workerId,
        string? reason,
        string? filter,
        string? entryId,
        CancellationToken cancellationToken)
    {
        var databasePath = pathResolver.Resolve();
        if (string.IsNullOrWhiteSpace(databasePath))
        {
            return 0;
        }

        await using var connection = OpenConnection(databasePath);
        await connection.OpenAsync(cancellationToken);

        await using var command = connection.CreateCommand();
        AddRunEntryFilterParameters(command, runId, bucket, workerId, reason, filter, entryId);

        command.CommandText =
            """
            SELECT COUNT(1)
            FROM run_entries e
            JOIN runs r ON r.run_id = e.run_id
            WHERE e.run_id = $runId
              AND ($bucket IS NULL OR e.bucket = $bucket)
              AND (
                $workerId IS NULL
                OR LOWER(COALESCE(e.worker_id, '')) LIKE $workerId ESCAPE '\'
                OR LOWER(COALESCE(e.sam_account_name, '')) LIKE $workerId ESCAPE '\'
              )
              AND ($reason IS NULL OR LOWER(COALESCE(e.reason, '')) = LOWER($reason))
              AND ($entryId IS NULL OR e.entry_id = $entryId)
              AND (
                $filter IS NULL
                OR LOWER(COALESCE(e.item_json, '')) LIKE $filter ESCAPE '\'
                OR LOWER(COALESCE(e.reason, '')) LIKE $filter ESCAPE '\'
                OR LOWER(COALESCE(e.review_case_type, '')) LIKE $filter ESCAPE '\'
              );
            """;

        var result = await command.ExecuteScalarAsync(cancellationToken);
        return result is null || result is DBNull ? 0 : Convert.ToInt32(result);
    }

    public async Task<IReadOnlyList<ChangedAttributeTotal>> GetRunEntryAttributeTotalsAsync(
        string runId,
        string? bucket,
        string? workerId,
        string? reason,
        string? filter,
        string? entryId,
        CancellationToken cancellationToken)
    {
        var databasePath = pathResolver.Resolve();
        if (string.IsNullOrWhiteSpace(databasePath))
        {
            return [];
        }

        await using var connection = OpenConnection(databasePath);
        await connection.OpenAsync(cancellationToken);

        await using var command = connection.CreateCommand();
        AddRunEntryFilterParameters(command, runId, bucket, workerId, reason, filter, entryId);
        command.CommandText =
            """
            SELECT
              e.entry_id,
              e.bucket,
              e.bucket_index,
              e.worker_id,
              e.sam_account_name,
              e.reason,
              e.review_category,
              e.review_case_type,
              e.started_at,
              e.item_json,
              r.run_id,
              r.artifact_type,
              r.mode,
              r.report_json
            FROM run_entries e
            JOIN runs r ON r.run_id = e.run_id
            WHERE e.run_id = $runId
              AND ($bucket IS NULL OR e.bucket = $bucket)
              AND (
                $workerId IS NULL
                OR LOWER(COALESCE(e.worker_id, '')) LIKE $workerId ESCAPE '\'
                OR LOWER(COALESCE(e.sam_account_name, '')) LIKE $workerId ESCAPE '\'
              )
              AND ($reason IS NULL OR LOWER(COALESCE(e.reason, '')) = LOWER($reason))
              AND ($entryId IS NULL OR e.entry_id = $entryId)
              AND (
                $filter IS NULL
                OR LOWER(COALESCE(e.item_json, '')) LIKE $filter ESCAPE '\'
                OR LOWER(COALESCE(e.reason, '')) LIKE $filter ESCAPE '\'
                OR LOWER(COALESCE(e.review_case_type, '')) LIKE $filter ESCAPE '\'
              )
            ORDER BY e.bucket ASC, e.bucket_index ASC, e.entry_id ASC;
            """;

        var firstSeenLabels = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var row = MapEntryRow(reader);
            var item = string.IsNullOrWhiteSpace(row.ItemJson) ? EmptyObject() : ParseJson(row.ItemJson);
            var report = string.IsNullOrWhiteSpace(row.ReportJson) ? EmptyObject() : ParseJson(row.ReportJson);
            var operations = GetOperations(report);
            var operation = FindOperation(operations, row.WorkerId, row.Bucket);

            foreach (var diffRow in GetDiffRows(item, operation, row.WorkerId, row.Bucket ?? "unknown").Where(diffRow => diffRow.Changed))
            {
                if (!firstSeenLabels.ContainsKey(diffRow.Attribute))
                {
                    firstSeenLabels[diffRow.Attribute] = diffRow.Attribute;
                }

                counts[diffRow.Attribute] = counts.GetValueOrDefault(diffRow.Attribute) + 1;
            }
        }

        return counts
            .Select(pair => new ChangedAttributeTotal(
                firstSeenLabels.TryGetValue(pair.Key, out var label) ? label : pair.Key,
                pair.Value))
            .OrderByDescending(total => total.Count)
            .ThenBy(total => total.Attribute, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static int Sum(params int[] values) => values.Sum();

    private static void AddRunEntryFilterParameters(
        SqliteCommand command,
        string runId,
        string? bucket,
        string? workerId,
        string? reason,
        string? filter,
        string? entryId)
    {
        command.Parameters.AddWithValue("$runId", runId);
        command.Parameters.AddWithValue("$bucket", string.IsNullOrWhiteSpace(bucket) ? DBNull.Value : bucket);
        command.Parameters.AddWithValue(
            "$workerId",
            string.IsNullOrWhiteSpace(workerId)
                ? DBNull.Value
                : $"%{EscapeLike(workerId.ToLowerInvariant())}%");
        command.Parameters.AddWithValue("$reason", string.IsNullOrWhiteSpace(reason) ? DBNull.Value : reason);
        command.Parameters.AddWithValue("$entryId", string.IsNullOrWhiteSpace(entryId) ? DBNull.Value : entryId);
        command.Parameters.AddWithValue(
            "$filter",
            string.IsNullOrWhiteSpace(filter)
                ? DBNull.Value
                : $"%{EscapeLike(filter.ToLowerInvariant())}%");
    }

    private static DateTimeOffset? ParseDate(string? value)
    {
        return DateTimeOffset.TryParse(value, out var parsed) ? parsed : null;
    }

    private static JsonElement ParseJson(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    private static RunSummary MapRunSummary(RunRow row)
    {
        return new RunSummary(
            RunId: row.RunId!,
            Path: row.Path,
            ArtifactType: row.ArtifactType ?? "SyncReport",
            ConfigPath: row.ConfigPath,
            MappingConfigPath: row.MappingConfigPath,
            Mode: row.Mode ?? "Unknown",
            DryRun: row.DryRun != 0,
            RunTrigger: row.RunTrigger ?? "AdHoc",
            RequestedBy: row.RequestedBy,
            Status: row.Status ?? "Unknown",
            StartedAt: ParseDate(row.StartedAt) ?? DateTimeOffset.MinValue,
            CompletedAt: ParseDate(row.CompletedAt),
            DurationSeconds: row.DurationSeconds,
            ProcessedWorkers: Sum(row.Creates, row.Updates, row.Enables, row.Disables, row.GraveyardMoves, row.Deletions, row.Unchanged),
            TotalWorkers: Sum(row.Creates, row.Updates, row.Enables, row.Disables, row.GraveyardMoves, row.Deletions, row.Quarantined, row.Conflicts, row.GuardrailFailures, row.ManualReview, row.Unchanged),
            Creates: row.Creates,
            Updates: row.Updates,
            Enables: row.Enables,
            Disables: row.Disables,
            GraveyardMoves: row.GraveyardMoves,
            Deletions: row.Deletions,
            Quarantined: row.Quarantined,
            Conflicts: row.Conflicts,
            GuardrailFailures: row.GuardrailFailures,
            ManualReview: row.ManualReview,
            Unchanged: row.Unchanged,
            SyncScope: ResolveSyncScope(row.Mode, row.ArtifactType, row.ReportJson));
    }

    private static string ResolveSyncScope(string? mode, string? artifactType, string? reportJson)
    {
        if (!string.IsNullOrWhiteSpace(reportJson))
        {
            try
            {
                using var document = JsonDocument.Parse(reportJson);
                if (document.RootElement.ValueKind == JsonValueKind.Object &&
                    document.RootElement.TryGetProperty("syncScope", out var syncScopeElement) &&
                    syncScopeElement.ValueKind == JsonValueKind.String)
                {
                    var value = syncScopeElement.GetString();
                    if (!string.IsNullOrWhiteSpace(value))
                    {
                        return value;
                    }
                }
            }
            catch (JsonException)
            {
            }
        }

        if (string.Equals(artifactType, "FullSync", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(mode, "FullSyncDryRun", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(mode, "FullSyncLive", StringComparison.OrdinalIgnoreCase))
        {
            return "Full sync";
        }

        if (string.Equals(mode, "BulkSync", StringComparison.OrdinalIgnoreCase))
        {
            return "Bulk full scan";
        }

        if (string.Equals(mode, "DeleteAllUsers", StringComparison.OrdinalIgnoreCase))
        {
            return "Delete all users";
        }

        return "Unknown";
    }

    private static IReadOnlyDictionary<string, int> BuildBucketCounts(RunSummary run)
    {
        return new Dictionary<string, int>
        {
            ["quarantined"] = run.Quarantined,
            ["conflicts"] = run.Conflicts,
            ["manualReview"] = run.ManualReview,
            ["guardrailFailures"] = run.GuardrailFailures,
            ["creates"] = run.Creates,
            ["updates"] = run.Updates,
            ["enables"] = run.Enables,
            ["disables"] = run.Disables,
            ["graveyardMoves"] = run.GraveyardMoves,
            ["deletions"] = run.Deletions,
            ["unchanged"] = run.Unchanged,
        };
    }

    private static RunEntry MapEntry(EntryRow row)
    {
        var item = string.IsNullOrWhiteSpace(row.ItemJson) ? EmptyObject() : ParseJson(row.ItemJson);
        var report = string.IsNullOrWhiteSpace(row.ReportJson) ? EmptyObject() : ParseJson(row.ReportJson);
        var operations = GetOperations(report);
        var operation = FindOperation(operations, row.WorkerId, row.Bucket);
        var diffRows = GetDiffRows(item, operation, row.WorkerId, row.Bucket ?? "unknown");
        var topChangedAttributes = diffRows
            .Where(diff => diff.Changed)
            .Select(diff => diff.Attribute)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(4)
            .ToArray();
        var primarySummary = GetPrimarySummary(row.Bucket ?? "unknown", row.Reason, row.ReviewCaseType, diffRows, item, operation);
        var failureSummary = GetFailureSummary(row.Bucket ?? "unknown", row.Reason, row.ReviewCaseType, item);
        return new RunEntry(
            EntryId: row.EntryId ?? $"{row.RunId}:{row.Bucket}:{row.WorkerId ?? row.SamAccountName ?? "unknown"}:{row.BucketIndex}",
            RunId: row.RunId ?? string.Empty,
            ArtifactType: row.ArtifactType ?? "SyncReport",
            Mode: row.Mode ?? "Unknown",
            Bucket: row.Bucket ?? "unknown",
            BucketLabel: BucketLabels.TryGetValue(row.Bucket ?? string.Empty, out var label) ? label : row.Bucket ?? "unknown",
            WorkerId: row.WorkerId,
            SamAccountName: row.SamAccountName,
            Reason: row.Reason,
            ReviewCategory: row.ReviewCategory,
            ReviewCaseType: row.ReviewCaseType,
            StartedAt: ParseDate(row.StartedAt),
            ChangeCount: diffRows.Count(row => row.Changed),
            OperationSummary: GetOperationSummary(row.Bucket ?? "unknown", item, operation),
            FailureSummary: failureSummary,
            PrimarySummary: primarySummary,
            TopChangedAttributes: topChangedAttributes,
            DiffRows: diffRows,
            Item: item);
    }

    private static IReadOnlyList<JsonElement> GetOperations(JsonElement report)
    {
        if (report.ValueKind != JsonValueKind.Object || !report.TryGetProperty("operations", out var operations) || operations.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        return operations.EnumerateArray().Select(operation => operation.Clone()).ToArray();
    }

    private static JsonElement? FindOperation(IReadOnlyList<JsonElement> operations, string? workerId, string? bucket)
    {
        foreach (var operation in operations)
        {
            if (GetString(operation, "workerId") == workerId && GetString(operation, "bucket") == bucket)
            {
                return operation;
            }
        }

        return null;
    }

    private static IReadOnlyList<DiffRow> GetDiffRows(JsonElement item, JsonElement? operation, string? workerId, string bucket)
    {
        var changedRows = GetArray(item, "changedAttributeDetails")
            .Select(row => new DiffRow(
                Attribute: GetString(row, "targetAttribute") ?? "attribute",
                Source: GetString(row, "sourceField"),
                Before: InlineValue(GetProperty(row, "currentAdValue")),
                After: InlineValue(GetProperty(row, "proposedValue")),
                Changed: true))
            .ToArray();
        if (changedRows.Length > 0)
        {
            return changedRows;
        }

        var attributeRows = GetArray(item, "attributeRows")
            .Select(row => new DiffRow(
                Attribute: GetString(row, "targetAttribute") ?? "attribute",
                Source: GetString(row, "sourceField"),
                Before: InlineValue(GetProperty(row, "currentAdValue")),
                After: InlineValue(GetProperty(row, "proposedValue")),
                Changed: GetBoolean(row, "changed") ?? false))
            .ToArray();
        if (attributeRows.Length > 0)
        {
            return attributeRows;
        }

        if (operation is not null)
        {
            var before = GetObject(operation.Value, "before");
            var after = GetObject(operation.Value, "after");
            var keys = GetObjectProperties(before).Select(property => property.Name)
                .Concat(GetObjectProperties(after).Select(property => property.Name))
                .Distinct(StringComparer.Ordinal)
                .ToArray();
            if (keys.Length > 0)
            {
                return keys.Select(key =>
                {
                    var beforeValue = GetProperty(before, key);
                    var afterValue = GetProperty(after, key);
                    var beforeText = InlineValue(beforeValue);
                    var afterText = InlineValue(afterValue);
                    return new DiffRow(
                        Attribute: key,
                        Source: null,
                        Before: beforeText,
                        After: afterText,
                        Changed: beforeText != afterText);
                }).ToArray();
            }
        }

        return GetSemanticDiffRows(item, workerId, bucket);
    }

    private static IReadOnlyList<DiffRow> GetSemanticDiffRows(JsonElement item, string? workerId, string bucket)
    {
        _ = workerId;
        var currentEnabled = GetBoolean(item, "currentEnabled");
        var proposedEnable = ResolveProposedEnable(item, bucket);
        if (currentEnabled.HasValue && proposedEnable.HasValue && currentEnabled != proposedEnable)
        {
            return
            [
                new DiffRow(
                    Attribute: "enabled",
                    Source: null,
                    Before: InlineScalar(currentEnabled.Value),
                    After: InlineScalar(proposedEnable.Value),
                    Changed: true)
            ];
        }

        return [];
    }

    private static bool? ResolveProposedEnable(JsonElement item, string bucket)
    {
        var explicitValue = GetBoolean(item, "proposedEnable");
        if (explicitValue.HasValue)
        {
            return explicitValue.Value;
        }
        if (string.Equals(bucket, "disables", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }
        if (string.Equals(bucket, "enables", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }
        return null;
    }

    private static OperationSummary? GetOperationSummary(string bucket, JsonElement item, JsonElement? operation)
    {
        var targetSam = GetString(GetObject(operation, "target"), "samAccountName")
            ?? GetString(item, "samAccountName")
            ?? "user";
        var operationType = GetString(operation, "operationType");

        return operationType switch
        {
            "DisableUser" => new OperationSummary($"Disable account {targetSam}", "Account sign-in will be turned off.", null, null, null),
            "EnableUser" => new OperationSummary($"Enable account {targetSam}", "Account sign-in will be turned on.", null, null, null),
            "MoveUser" => new OperationSummary(
                $"Move account {targetSam}",
                null,
                GetString(GetObject(operation, "after"), "targetOu") ?? GetString(item, "targetOu"),
                GetString(GetObject(operation, "before"), "parentOu"),
                GetString(GetObject(operation, "after"), "targetOu") ?? GetString(item, "targetOu")),
            "CreateUser" => new OperationSummary(
                $"Create account {targetSam}",
                null,
                GetString(GetObject(operation, "after"), "targetOu") ?? GetString(item, "targetOu"),
                null,
                null),
            "DeleteUser" => new OperationSummary($"Delete account {targetSam}", "The AD user object will be removed.", null, null, null),
            "UpdateAttributes" => new OperationSummary(
                $"Update attributes for {targetSam}",
                $"{GetDiffRows(item, operation, GetString(item, "workerId"), bucket).Count(row => row.Changed)} attribute changes.",
                null,
                null,
                null),
            _ when bucket is "quarantined" or "manualReview" or "conflicts" or "guardrailFailures"
                => new OperationSummary(
                    BucketLabels.TryGetValue(bucket, out var label) ? label : bucket,
                    GetString(item, "reason") ?? GetString(item, "reviewCaseType"),
                    null,
                    null,
                    null),
            _ => null,
        };
    }

    private static string? GetPrimarySummary(
        string bucket,
        string? reason,
        string? reviewCaseType,
        IReadOnlyList<DiffRow> diffRows,
        JsonElement item,
        JsonElement? operation)
    {
        if (!string.IsNullOrWhiteSpace(reason))
        {
            return reason;
        }

        if (!string.IsNullOrWhiteSpace(reviewCaseType))
        {
            return reviewCaseType;
        }

        var operationSummary = GetOperationSummary(bucket, item, operation);
        if (!string.IsNullOrWhiteSpace(operationSummary?.Effect))
        {
            return operationSummary.Effect;
        }

        var changed = diffRows.Count(row => row.Changed);
        return changed > 0 ? $"{changed} changed attributes." : null;
    }

    private static string? GetFailureSummary(string bucket, string? reason, string? reviewCaseType, JsonElement item)
    {
        if (bucket is "conflicts" or "manualReview" or "guardrailFailures" or "quarantined")
        {
            return reason ?? reviewCaseType ?? GetString(item, "message");
        }

        if (item.ValueKind == JsonValueKind.Object && item.TryGetProperty("succeeded", out var succeeded) && succeeded.ValueKind == JsonValueKind.False)
        {
            return reason ?? GetString(item, "message") ?? "The operation failed.";
        }

        return null;
    }

    private static JsonElement? GetObject(JsonElement? element, string propertyName)
    {
        if (element is null || element.Value.ValueKind != JsonValueKind.Object || !element.Value.TryGetProperty(propertyName, out var property) || property.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        return property.Clone();
    }

    private static IEnumerable<JsonProperty> GetObjectProperties(JsonElement? element)
    {
        return element is not null && element.Value.ValueKind == JsonValueKind.Object
            ? element.Value.EnumerateObject()
            : [];
    }

    private static JsonElement? GetProperty(JsonElement element, string propertyName)
    {
        if (element.ValueKind == JsonValueKind.Object && element.TryGetProperty(propertyName, out var property))
        {
            return property.Clone();
        }

        return null;
    }

    private static JsonElement? GetProperty(JsonElement? element, string propertyName)
    {
        return element is null ? null : GetProperty(element.Value, propertyName);
    }

    private static string? GetString(JsonElement? element, string propertyName)
    {
        var property = element is null ? null : GetProperty(element.Value, propertyName);
        return property is null ? null : AsString(property.Value);
    }

    private static string? GetString(JsonElement element, string propertyName)
    {
        return GetString((JsonElement?)element, propertyName);
    }

    private static bool? GetBoolean(JsonElement element, string propertyName)
    {
        var property = GetProperty(element, propertyName);
        return property is null ? null : AsBoolean(property.Value);
    }

    private static IReadOnlyList<JsonElement> GetArray(JsonElement element, string propertyName)
    {
        var property = GetProperty(element, propertyName);
        if (property is null || property.Value.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        return property.Value.EnumerateArray().Select(item => item.Clone()).ToArray();
    }

    private static string? AsString(JsonElement value)
    {
        return value.ValueKind switch
        {
            JsonValueKind.String => string.IsNullOrWhiteSpace(value.GetString()) ? null : value.GetString(),
            JsonValueKind.Number => value.ToString(),
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            _ => null,
        };
    }

    private static bool? AsBoolean(JsonElement value)
    {
        return value.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => null,
        };
    }

    private static string InlineValue(JsonElement? value)
    {
        if (value is null)
        {
            return "(unset)";
        }

        return value.Value.ValueKind switch
        {
            JsonValueKind.Null => "(unset)",
            JsonValueKind.Undefined => "(unset)",
            JsonValueKind.String => string.IsNullOrEmpty(value.Value.GetString()) ? "(unset)" : value.Value.GetString()!,
            JsonValueKind.Array => string.Join(", ", value.Value.EnumerateArray().Select(item => InlineValue(item.Clone()))),
            JsonValueKind.Object => value.Value.GetRawText(),
            _ => value.Value.ToString(),
        };
    }

    private static string InlineScalar(bool value) => value ? "true" : "false";

    private static JsonElement EmptyObject()
    {
        using var document = JsonDocument.Parse("{}");
        return document.RootElement.Clone();
    }

    private static string EscapeLike(string value)
    {
        return value.Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("%", "\\%", StringComparison.Ordinal)
            .Replace("_", "\\_", StringComparison.Ordinal);
    }

    private static SqliteConnection OpenConnection(string databasePath)
    {
        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
        }.ToString();
        return new SqliteConnection(connectionString);
    }

    private static SqliteConnection OpenWriteConnection(string databasePath)
    {
        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
        }.ToString();
        return new SqliteConnection(connectionString);
    }

    private static void BindRunRecord(SqliteCommand command, RunRecord run)
    {
        command.Parameters.AddWithValue("$runId", run.RunId);
        command.Parameters.AddWithValue("$path", (object?)run.Path ?? DBNull.Value);
        command.Parameters.AddWithValue("$artifactType", run.ArtifactType);
        command.Parameters.AddWithValue("$configPath", (object?)run.ConfigPath ?? DBNull.Value);
        command.Parameters.AddWithValue("$mappingConfigPath", (object?)run.MappingConfigPath ?? DBNull.Value);
        command.Parameters.AddWithValue("$mode", run.Mode);
        command.Parameters.AddWithValue("$dryRun", run.DryRun ? 1 : 0);
        command.Parameters.AddWithValue("$runTrigger", run.RunTrigger);
        command.Parameters.AddWithValue("$requestedBy", (object?)run.RequestedBy ?? DBNull.Value);
        command.Parameters.AddWithValue("$status", run.Status);
        command.Parameters.AddWithValue("$startedAt", run.StartedAt.ToString("O"));
        command.Parameters.AddWithValue("$completedAt", ToDbValue(run.CompletedAt));
        command.Parameters.AddWithValue("$durationSeconds", (object?)run.DurationSeconds ?? DBNull.Value);
        command.Parameters.AddWithValue("$creates", run.Creates);
        command.Parameters.AddWithValue("$updates", run.Updates);
        command.Parameters.AddWithValue("$enables", run.Enables);
        command.Parameters.AddWithValue("$disables", run.Disables);
        command.Parameters.AddWithValue("$graveyardMoves", run.GraveyardMoves);
        command.Parameters.AddWithValue("$deletions", run.Deletions);
        command.Parameters.AddWithValue("$quarantined", run.Quarantined);
        command.Parameters.AddWithValue("$conflicts", run.Conflicts);
        command.Parameters.AddWithValue("$guardrailFailures", run.GuardrailFailures);
        command.Parameters.AddWithValue("$manualReview", run.ManualReview);
        command.Parameters.AddWithValue("$unchanged", run.Unchanged);
        command.Parameters.AddWithValue("$reportJson", run.Report.GetRawText());
    }

    private static object ToDbValue(DateTimeOffset? value) => value?.ToString("O") ?? (object)DBNull.Value;

    private static RunRow MapRunRow(SqliteDataReader reader)
    {
        var row = new RunRow
        {
            RunId = reader.GetStringOrDefault("run_id"),
            Path = reader.GetStringOrDefault("path"),
            ArtifactType = reader.GetStringOrDefault("artifact_type"),
            ConfigPath = reader.GetStringOrDefault("config_path"),
            MappingConfigPath = reader.GetStringOrDefault("mapping_config_path"),
            Mode = reader.GetStringOrDefault("mode"),
            DryRun = reader.GetInt32OrDefault("dry_run"),
            RunTrigger = reader.GetStringOrDefault("run_trigger"),
            RequestedBy = reader.GetStringOrDefault("requested_by"),
            Status = reader.GetStringOrDefault("status"),
            StartedAt = reader.GetStringOrDefault("started_at"),
            CompletedAt = reader.GetStringOrDefault("completed_at"),
            DurationSeconds = reader.GetNullableInt32("duration_seconds"),
            Creates = reader.GetInt32OrDefault("creates"),
            Updates = reader.GetInt32OrDefault("updates"),
            Enables = reader.GetInt32OrDefault("enables"),
            Disables = reader.GetInt32OrDefault("disables"),
            GraveyardMoves = reader.GetInt32OrDefault("graveyard_moves"),
            Deletions = reader.GetInt32OrDefault("deletions"),
            Quarantined = reader.GetInt32OrDefault("quarantined"),
            Conflicts = reader.GetInt32OrDefault("conflicts"),
            GuardrailFailures = reader.GetInt32OrDefault("guardrail_failures"),
            ManualReview = reader.GetInt32OrDefault("manual_review"),
            Unchanged = reader.GetInt32OrDefault("unchanged"),
            ReportJson = reader.GetStringOrDefault("report_json"),
            EntryCount = reader.GetInt32OrDefault("entry_count"),
            EntryCreates = reader.GetInt32OrDefault("entry_creates"),
            EntryUpdates = reader.GetInt32OrDefault("entry_updates"),
            EntryEnables = reader.GetInt32OrDefault("entry_enables"),
            EntryDisables = reader.GetInt32OrDefault("entry_disables"),
            EntryGraveyardMoves = reader.GetInt32OrDefault("entry_graveyard_moves"),
            EntryDeletions = reader.GetInt32OrDefault("entry_deletions"),
            EntryQuarantined = reader.GetInt32OrDefault("entry_quarantined"),
            EntryConflicts = reader.GetInt32OrDefault("entry_conflicts"),
            EntryGuardrailFailures = reader.GetInt32OrDefault("entry_guardrail_failures"),
            EntryManualReview = reader.GetInt32OrDefault("entry_manual_review"),
            EntryUnchanged = reader.GetInt32OrDefault("entry_unchanged"),
        };

        return BackfillBucketCountsFromEntries(row);
    }

    private static RunWithReportRow MapRunWithReportRow(SqliteDataReader reader)
    {
        var row = MapRunRow(reader);
        return new RunWithReportRow
        {
            RunId = row.RunId,
            Path = row.Path,
            ArtifactType = row.ArtifactType,
            ConfigPath = row.ConfigPath,
            MappingConfigPath = row.MappingConfigPath,
            Mode = row.Mode,
            DryRun = row.DryRun,
            RunTrigger = row.RunTrigger,
            RequestedBy = row.RequestedBy,
            Status = row.Status,
            StartedAt = row.StartedAt,
            CompletedAt = row.CompletedAt,
            DurationSeconds = row.DurationSeconds,
            Creates = row.Creates,
            Updates = row.Updates,
            Enables = row.Enables,
            Disables = row.Disables,
            GraveyardMoves = row.GraveyardMoves,
            Deletions = row.Deletions,
            Quarantined = row.Quarantined,
            Conflicts = row.Conflicts,
            GuardrailFailures = row.GuardrailFailures,
            ManualReview = row.ManualReview,
            Unchanged = row.Unchanged,
            ReportJson = reader.GetStringOrDefault("report_json"),
        };
    }

    private static EntryRow MapEntryRow(SqliteDataReader reader)
    {
        return new EntryRow
        {
            EntryId = reader.GetStringOrDefault("entry_id"),
            RunId = reader.GetStringOrDefault("run_id"),
            ArtifactType = reader.GetStringOrDefault("artifact_type"),
            Mode = reader.GetStringOrDefault("mode"),
            Bucket = reader.GetStringOrDefault("bucket"),
            BucketIndex = reader.GetInt32OrDefault("bucket_index"),
            WorkerId = reader.GetStringOrDefault("worker_id"),
            SamAccountName = reader.GetStringOrDefault("sam_account_name"),
            Reason = reader.GetStringOrDefault("reason"),
            ReviewCategory = reader.GetStringOrDefault("review_category"),
            ReviewCaseType = reader.GetStringOrDefault("review_case_type"),
            StartedAt = reader.GetStringOrDefault("started_at"),
            ItemJson = reader.GetStringOrDefault("item_json"),
            ReportJson = reader.GetStringOrDefault("report_json"),
        };
    }

    private class RunRow
    {
        public string? RunId { get; init; }
        public string? Path { get; init; }
        public string? ArtifactType { get; init; }
        public string? ConfigPath { get; init; }
        public string? MappingConfigPath { get; init; }
        public string? Mode { get; init; }
        public int DryRun { get; init; }
        public string? RunTrigger { get; init; }
        public string? RequestedBy { get; init; }
        public string? Status { get; init; }
        public string? StartedAt { get; init; }
        public string? CompletedAt { get; init; }
        public int? DurationSeconds { get; init; }
        public int Creates { get; init; }
        public int Updates { get; init; }
        public int Enables { get; init; }
        public int Disables { get; init; }
        public int GraveyardMoves { get; init; }
        public int Deletions { get; init; }
        public int Quarantined { get; init; }
        public int Conflicts { get; init; }
        public int GuardrailFailures { get; init; }
        public int ManualReview { get; init; }
        public int Unchanged { get; init; }
        public string? ReportJson { get; init; }
        public int EntryCount { get; init; }
        public int EntryCreates { get; init; }
        public int EntryUpdates { get; init; }
        public int EntryEnables { get; init; }
        public int EntryDisables { get; init; }
        public int EntryGraveyardMoves { get; init; }
        public int EntryDeletions { get; init; }
        public int EntryQuarantined { get; init; }
        public int EntryConflicts { get; init; }
        public int EntryGuardrailFailures { get; init; }
        public int EntryManualReview { get; init; }
        public int EntryUnchanged { get; init; }
    }

    private static RunRow BackfillBucketCountsFromEntries(RunRow row)
    {
        var materializedTotal = Sum(
            row.Creates,
            row.Updates,
            row.Enables,
            row.Disables,
            row.GraveyardMoves,
            row.Deletions,
            row.Quarantined,
            row.Conflicts,
            row.GuardrailFailures,
            row.ManualReview,
            row.Unchanged);

        if (row.EntryCount <= materializedTotal)
        {
            return row;
        }

        return new RunRow
        {
            RunId = row.RunId,
            Path = row.Path,
            ArtifactType = row.ArtifactType,
            ConfigPath = row.ConfigPath,
            MappingConfigPath = row.MappingConfigPath,
            Mode = row.Mode,
            DryRun = row.DryRun,
            RunTrigger = row.RunTrigger,
            RequestedBy = row.RequestedBy,
            Status = row.Status,
            StartedAt = row.StartedAt,
            CompletedAt = row.CompletedAt,
            DurationSeconds = row.DurationSeconds,
            Creates = row.EntryCreates,
            Updates = row.EntryUpdates,
            Enables = row.EntryEnables,
            Disables = row.EntryDisables,
            GraveyardMoves = row.EntryGraveyardMoves,
            Deletions = row.EntryDeletions,
            Quarantined = row.EntryQuarantined,
            Conflicts = row.EntryConflicts,
            GuardrailFailures = row.EntryGuardrailFailures,
            ManualReview = row.EntryManualReview,
            Unchanged = row.EntryUnchanged,
            ReportJson = row.ReportJson,
            EntryCount = row.EntryCount,
            EntryCreates = row.EntryCreates,
            EntryUpdates = row.EntryUpdates,
            EntryEnables = row.EntryEnables,
            EntryDisables = row.EntryDisables,
            EntryGraveyardMoves = row.EntryGraveyardMoves,
            EntryDeletions = row.EntryDeletions,
            EntryQuarantined = row.EntryQuarantined,
            EntryConflicts = row.EntryConflicts,
            EntryGuardrailFailures = row.EntryGuardrailFailures,
            EntryManualReview = row.EntryManualReview,
            EntryUnchanged = row.EntryUnchanged,
        };
    }

    private sealed class RunWithReportRow : RunRow
    {
    }

    private sealed class EntryRow
    {
        public string? EntryId { get; init; }
        public string? RunId { get; init; }
        public string? ArtifactType { get; init; }
        public string? Mode { get; init; }
        public string? Bucket { get; init; }
        public int BucketIndex { get; init; }
        public string? WorkerId { get; init; }
        public string? SamAccountName { get; init; }
        public string? Reason { get; init; }
        public string? ReviewCategory { get; init; }
        public string? ReviewCaseType { get; init; }
        public string? StartedAt { get; init; }
        public string? ItemJson { get; init; }
        public string? ReportJson { get; init; }
    }
}

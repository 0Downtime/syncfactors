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
            SELECT
              run_id,
              path,
              artifact_type,
              config_path,
              mapping_config_path,
              mode,
              dry_run,
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
              unchanged
            FROM runs
            ORDER BY COALESCE(started_at, '') DESC, COALESCE(path, '') DESC
            LIMIT 25;
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
                Unchanged: row.Unchanged));
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
              run_id,
              path,
              artifact_type,
              config_path,
              mapping_config_path,
              mode,
              dry_run,
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
            WHERE run_id = $runId
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

    public async Task<IReadOnlyList<RunEntry>> GetRunEntriesAsync(
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
        var where = new List<string> { "e.run_id = $runId" };
        command.Parameters.AddWithValue("$runId", runId);

        if (!string.IsNullOrWhiteSpace(bucket))
        {
            where.Add("e.bucket = $bucket");
            command.Parameters.AddWithValue("$bucket", bucket);
        }
        if (!string.IsNullOrWhiteSpace(workerId))
        {
            where.Add("e.worker_id = $workerId");
            command.Parameters.AddWithValue("$workerId", workerId);
        }
        if (!string.IsNullOrWhiteSpace(reason))
        {
            where.Add("LOWER(COALESCE(e.reason, '')) = LOWER($reason)");
            command.Parameters.AddWithValue("$reason", reason);
        }
        if (!string.IsNullOrWhiteSpace(entryId))
        {
            where.Add("e.entry_id = $entryId");
            command.Parameters.AddWithValue("$entryId", entryId);
        }
        if (!string.IsNullOrWhiteSpace(filter))
        {
            where.Add("LOWER(COALESCE(e.item_json, '')) LIKE $filter ESCAPE '\\'");
            command.Parameters.AddWithValue("$filter", $"%{EscapeLike(filter.ToLowerInvariant())}%");
        }

        command.CommandText =
            $"""
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
            WHERE {string.Join("\n  AND ", where)}
            ORDER BY e.bucket ASC, e.bucket_index ASC, e.entry_id ASC;
            """;

        var entries = new List<RunEntry>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            entries.Add(MapEntry(MapEntryRow(reader)));
        }

        return entries;
    }

    private static int Sum(params int[] values) => values.Sum();

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
            Unchanged: row.Unchanged);
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
            Mode = SqliteOpenMode.ReadOnly,
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
        return new RunRow
        {
            RunId = reader.GetStringOrDefault("run_id"),
            Path = reader.GetStringOrDefault("path"),
            ArtifactType = reader.GetStringOrDefault("artifact_type"),
            ConfigPath = reader.GetStringOrDefault("config_path"),
            MappingConfigPath = reader.GetStringOrDefault("mapping_config_path"),
            Mode = reader.GetStringOrDefault("mode"),
            DryRun = reader.GetInt32OrDefault("dry_run"),
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
        };
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
    }

    private sealed class RunWithReportRow : RunRow
    {
        public string? ReportJson { get; init; }
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

using System.Text.Json;
using SyncFactors.Contracts;
using SyncFactors.Domain;

namespace SyncFactors.Infrastructure;

public sealed class SqliteRunRepository(SqlitePathResolver pathResolver, SqliteJsonShell sqlite) : IRunRepository
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

        var rows = await sqlite.QueryAsync<RunRow>(
            databasePath,
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
            """,
            cancellationToken);

        return rows
            .Where(row => !string.IsNullOrWhiteSpace(row.RunId))
            .Select(row => new RunSummary(
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
                Unchanged: row.Unchanged))
            .ToArray();
    }

    public async Task<RunDetail?> GetRunAsync(string runId, CancellationToken cancellationToken)
    {
        var databasePath = pathResolver.Resolve();
        if (string.IsNullOrWhiteSpace(databasePath))
        {
            return null;
        }

        var rows = await sqlite.QueryAsync<RunWithReportRow>(
            databasePath,
            $"""
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
            WHERE run_id = {Quote(runId)}
            LIMIT 1;
            """,
            cancellationToken);

        var row = rows.FirstOrDefault();
        if (row is null || string.IsNullOrWhiteSpace(row.RunId) || string.IsNullOrWhiteSpace(row.ReportJson))
        {
            return null;
        }

        var run = MapRunSummary(row);
        return new RunDetail(
            Run: run,
            Report: ParseJson(row.ReportJson),
            BucketCounts: BuildBucketCounts(run));
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

        var where = new List<string> { $"e.run_id = {Quote(runId)}" };
        if (!string.IsNullOrWhiteSpace(bucket))
        {
            where.Add($"e.bucket = {Quote(bucket)}");
        }
        if (!string.IsNullOrWhiteSpace(workerId))
        {
            where.Add($"e.worker_id = {Quote(workerId)}");
        }
        if (!string.IsNullOrWhiteSpace(reason))
        {
            where.Add($"LOWER(COALESCE(e.reason, '')) = LOWER({Quote(reason)})");
        }
        if (!string.IsNullOrWhiteSpace(entryId))
        {
            where.Add($"e.entry_id = {Quote(entryId)}");
        }
        if (!string.IsNullOrWhiteSpace(filter))
        {
            where.Add($"LOWER(COALESCE(e.item_json, '')) LIKE {Quote($"%{EscapeLike(filter.ToLowerInvariant())}%")} ESCAPE '\\'");
        }

        var rows = await sqlite.QueryAsync<EntryRow>(
            databasePath,
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
            """,
            cancellationToken);

        return rows.Select(MapEntry).ToArray();
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

    private static string Quote(string value) => $"'{value.Replace("'", "''")}'";

    private static string EscapeLike(string value)
    {
        return value.Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("%", "\\%", StringComparison.Ordinal)
            .Replace("_", "\\_", StringComparison.Ordinal);
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

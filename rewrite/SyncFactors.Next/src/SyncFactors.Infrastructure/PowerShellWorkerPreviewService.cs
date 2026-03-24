using System.Diagnostics;
using System.Text.Json;
using SyncFactors.Contracts;

namespace SyncFactors.Infrastructure;

public sealed class PowerShellWorkerPreviewService(SyncFactorsConfigPathResolver configResolver)
{
    public async Task<WorkerPreviewResult> PreviewAsync(string workerId, CancellationToken cancellationToken)
    {
        var configPath = configResolver.ResolveConfigPath();
        var mappingConfigPath = configResolver.ResolveMappingConfigPath();
        if (string.IsNullOrWhiteSpace(configPath) || string.IsNullOrWhiteSpace(mappingConfigPath))
        {
            throw new InvalidOperationException("SyncFactors preview config could not be resolved. Set SyncFactors__ConfigPath and SyncFactors__MappingConfigPath or create the local config files.");
        }

        var startInfo = new ProcessStartInfo
        {
            FileName = "pwsh",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };

        foreach (var arg in new[]
        {
            "-NoLogo",
            "-NoProfile",
            "-File",
            "scripts/Invoke-SyncFactorsWorkerPreview.ps1",
            "-ConfigPath",
            configPath,
            "-MappingConfigPath",
            mappingConfigPath,
            "-WorkerId",
            workerId,
            "-PreviewMode",
            "Full",
            "-AsJson",
        })
        {
            startInfo.ArgumentList.Add(arg);
        }

        using var process = new Process { StartInfo = startInfo };
        process.Start();

        var stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);

        await process.WaitForExitAsync(cancellationToken);

        var stdout = await stdoutTask;
        var stderr = await stderrTask;

        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException(string.IsNullOrWhiteSpace(stderr) ? $"Worker preview failed for {workerId}." : stderr.Trim());
        }

        using var document = JsonDocument.Parse(stdout);
        return Normalize(document.RootElement);
    }

    private static WorkerPreviewResult Normalize(JsonElement parsed)
    {
        var operations = GetArray(parsed, "operations");
        var changedAttributes = GetArray(parsed, "changedAttributes");
        var entries = GetArray(parsed, "entries")
            .Select(entry => new WorkerPreviewEntry(
                Bucket: GetString(entry, "bucket") ?? "unknown",
                Item: GetObject(entry, "item") ?? EmptyObject()))
            .ToArray();

        var diffRows = changedAttributes.Count > 0
            ? changedAttributes
                .Select(row => new DiffRow(
                    Attribute: GetString(row, "targetAttribute") ?? "attribute",
                    Source: GetString(row, "sourceField"),
                    Before: InlineValue(GetProperty(row, "currentAdValue")),
                    After: InlineValue(GetProperty(row, "proposedValue")),
                    Changed: InlineValue(GetProperty(row, "currentAdValue")) != InlineValue(GetProperty(row, "proposedValue"))))
                .Where(row => row.Changed)
                .ToArray()
            : GetDiffRowsFromPreviewEntries(entries, operations);

        var preview = GetObject(parsed, "preview") ?? EmptyObject();

        return new WorkerPreviewResult(
            ReportPath: GetString(parsed, "reportPath"),
            RunId: GetString(parsed, "runId"),
            Mode: GetString(parsed, "mode"),
            Status: GetString(parsed, "status"),
            ErrorMessage: GetString(parsed, "errorMessage"),
            ArtifactType: GetString(parsed, "artifactType"),
            SuccessFactorsAuth: GetString(parsed, "successFactorsAuth"),
            WorkerId: GetString(preview, "workerId") ?? string.Empty,
            Buckets: GetStringArray(preview, "buckets"),
            MatchedExistingUser: GetBoolean(preview, "matchedExistingUser"),
            ReviewCategory: GetString(preview, "reviewCategory"),
            ReviewCaseType: GetString(preview, "reviewCaseType"),
            Reason: GetString(preview, "reason"),
            OperatorActionSummary: GetString(preview, "operatorActionSummary"),
            SamAccountName: GetString(preview, "samAccountName"),
            TargetOu: GetString(preview, "targetOu"),
            CurrentDistinguishedName: GetString(preview, "currentDistinguishedName"),
            CurrentEnabled: GetBoolean(preview, "currentEnabled"),
            ProposedEnable: GetBoolean(preview, "proposedEnable"),
            OperationSummary: SummarizePreviewOperation(operations),
            DiffRows: diffRows,
            Entries: entries);
    }

    private static IReadOnlyList<DiffRow> GetDiffRowsFromPreviewEntries(
        IReadOnlyList<WorkerPreviewEntry> entries,
        IReadOnlyList<JsonElement> operations)
    {
        foreach (var entry in entries)
        {
            var changedRows = GetArray(entry.Item, "changedAttributeDetails")
                .Select(row => new DiffRow(
                    Attribute: GetString(row, "targetAttribute") ?? "attribute",
                    Source: GetString(row, "sourceField"),
                    Before: InlineValue(GetProperty(row, "currentAdValue")),
                    After: InlineValue(GetProperty(row, "proposedValue")),
                    Changed: InlineValue(GetProperty(row, "currentAdValue")) != InlineValue(GetProperty(row, "proposedValue"))))
                .Where(row => row.Changed)
                .ToArray();
            if (changedRows.Length > 0)
            {
                return changedRows;
            }

            var attributeRows = GetArray(entry.Item, "attributeRows")
                .Select(row => new DiffRow(
                    Attribute: GetString(row, "targetAttribute") ?? "attribute",
                    Source: GetString(row, "sourceField"),
                    Before: InlineValue(GetProperty(row, "currentAdValue")),
                    After: InlineValue(GetProperty(row, "proposedValue")),
                    Changed: GetBoolean(row, "changed") ?? false))
                .Where(row => row.Changed)
                .ToArray();
            if (attributeRows.Length > 0)
            {
                return attributeRows;
            }

            var semantic = GetSemanticPreviewDiffRows(entry.Item, entry.Bucket);
            if (semantic.Count > 0)
            {
                return semantic;
            }
        }

        return GetDiffRowsFromOperations(operations);
    }

    private static IReadOnlyList<DiffRow> GetSemanticPreviewDiffRows(JsonElement item, string bucket)
    {
        var currentEnabled = GetBoolean(item, "currentEnabled");
        var proposedEnable = ResolvePreviewProposedEnable(item, bucket);
        if (currentEnabled.HasValue && proposedEnable.HasValue && currentEnabled != proposedEnable)
        {
            return
            [
                new DiffRow("enabled", null, currentEnabled.Value ? "true" : "false", proposedEnable.Value ? "true" : "false", true)
            ];
        }

        return [];
    }

    private static bool? ResolvePreviewProposedEnable(JsonElement item, string bucket)
    {
        var explicitValue = GetBoolean(item, "proposedEnable");
        if (explicitValue.HasValue)
        {
            return explicitValue;
        }
        if (bucket == "disables")
        {
            return false;
        }
        if (bucket == "enables")
        {
            return true;
        }
        return null;
    }

    private static IReadOnlyList<DiffRow> GetDiffRowsFromOperations(IReadOnlyList<JsonElement> operations)
    {
        var rows = new List<DiffRow>();
        foreach (var operation in operations)
        {
            var before = GetObject(operation, "before");
            var after = GetObject(operation, "after");
            var keys = GetObjectProperties(before).Select(property => property.Name)
                .Concat(GetObjectProperties(after).Select(property => property.Name))
                .Distinct(StringComparer.Ordinal);
            foreach (var key in keys)
            {
                var beforeText = InlineValue(GetProperty(before, key));
                var afterText = InlineValue(GetProperty(after, key));
                if (beforeText != afterText)
                {
                    rows.Add(new DiffRow(key, null, beforeText, afterText, true));
                }
            }
        }

        return rows;
    }

    private static OperationSummary? SummarizePreviewOperation(IReadOnlyList<JsonElement> operations)
    {
        var operation = operations.FirstOrDefault();
        if (operation.ValueKind == JsonValueKind.Undefined)
        {
            return null;
        }

        var before = GetObject(operation, "before");
        var after = GetObject(operation, "after");
        return new OperationSummary(
            Action: GetString(operation, "operationType") ?? "Preview",
            Effect: null,
            TargetOu: GetString(after, "targetOu"),
            FromOu: GetString(before, "parentOu"),
            ToOu: GetString(after, "targetOu"));
    }

    private static IReadOnlyList<JsonElement> GetArray(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var property) || property.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        return property.EnumerateArray().Select(item => item.Clone()).ToArray();
    }

    private static JsonElement? GetObject(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var property) || property.ValueKind != JsonValueKind.Object)
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
        return element.ValueKind == JsonValueKind.Object && element.TryGetProperty(propertyName, out var property)
            ? property.Clone()
            : null;
    }

    private static JsonElement? GetProperty(JsonElement? element, string propertyName)
    {
        return element is null ? null : GetProperty(element.Value, propertyName);
    }

    private static string? GetString(JsonElement element, string propertyName) => GetString(GetProperty(element, propertyName));

    private static string? GetString(JsonElement? element, string propertyName) => element is null ? null : GetString(element.Value, propertyName);

    private static string? GetString(JsonElement? element)
    {
        if (element is null)
        {
            return null;
        }

        return element.Value.ValueKind switch
        {
            JsonValueKind.String => string.IsNullOrWhiteSpace(element.Value.GetString()) ? null : element.Value.GetString(),
            JsonValueKind.Number => element.Value.ToString(),
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            _ => null,
        };
    }

    private static bool? GetBoolean(JsonElement element, string propertyName)
    {
        var property = GetProperty(element, propertyName);
        return property is null ? null : property.Value.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => null,
        };
    }

    private static IReadOnlyList<string> GetStringArray(JsonElement element, string propertyName)
    {
        return GetArray(element, propertyName)
            .Select(item => GetString((JsonElement?)item))
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Cast<string>()
            .ToArray();
    }

    private static string InlineValue(JsonElement? value)
    {
        if (value is null || value.Value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            return "(unset)";
        }
        if (value.Value.ValueKind == JsonValueKind.String)
        {
            return string.IsNullOrEmpty(value.Value.GetString()) ? "(unset)" : value.Value.GetString()!;
        }
        if (value.Value.ValueKind == JsonValueKind.Array)
        {
            return string.Join(", ", value.Value.EnumerateArray().Select(item => InlineValue(item.Clone())));
        }
        if (value.Value.ValueKind == JsonValueKind.Object)
        {
            return value.Value.GetRawText();
        }
        return value.Value.ToString();
    }

    private static JsonElement EmptyObject()
    {
        using var document = JsonDocument.Parse("{}");
        return document.RootElement.Clone();
    }
}

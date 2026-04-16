using SyncFactors.Contracts;
using Microsoft.Extensions.Logging;
using System.Text.Json;

namespace SyncFactors.Domain;

public sealed class WorkerPreviewPlanner(
    IWorkerSource workerSource,
    IWorkerPlanningService planningService,
    IAttributeMappingProvider attributeMappingProvider,
    IWorkerPreviewLogWriter previewLogWriter,
    IRunRepository runRepository,
    ILogger<WorkerPreviewPlanner> logger) : IWorkerPreviewPlanner
{
    public async Task<WorkerPreviewResult> PreviewAsync(string workerId, CancellationToken cancellationToken)
    {
        logger.LogInformation("Starting worker preview.");
        var startedAt = DateTimeOffset.UtcNow;
        var worker = await workerSource.GetWorkerAsync(workerId, cancellationToken);
        if (worker is null)
        {
            logger.LogWarning("Worker preview could not resolve worker.");
            throw new InvalidOperationException($"Worker {workerId} could not be resolved.");
        }

        var logPath = previewLogWriter.CreateLogPath(workerId, startedAt);
        var plan = await planningService.PlanAsync(worker, logPath, cancellationToken);
        logger.LogInformation(
            "Worker preview completed planning. Bucket={Bucket} MatchedExistingUser={MatchedExistingUser} DiffCount={DiffCount}",
            plan.Bucket,
            plan.Identity.MatchedExistingUser,
            plan.AttributeChanges.Count(change => change.Changed));
        var preview = BuildPreview(
            plan,
            logPath,
            attributeMappingProvider.GetEnabledMappings());
        var history = await runRepository.ListWorkerPreviewHistoryAsync(worker.WorkerId, 1, cancellationToken);
        var previewWithHistory = preview with { PreviousRunId = history.FirstOrDefault()?.RunId };
        var fingerprint = WorkerPreviewFingerprint.Compute(previewWithHistory);
        var previewRunId = $"preview-{worker.WorkerId}-{startedAt:yyyyMMddHHmmssfff}";
        var finalizedPreview = previewWithHistory with
        {
            RunId = previewRunId,
            Fingerprint = fingerprint
        };

        await PersistPreviewAsync(finalizedPreview, startedAt, cancellationToken);
        return finalizedPreview;
    }

    private async Task PersistPreviewAsync(
        WorkerPreviewResult preview,
        DateTimeOffset startedAt,
        CancellationToken cancellationToken)
    {
        var report = ParseJson(
            "{"
            + "\"kind\":\"workerPreview\","
            + $"\"fingerprint\":\"{Escape(preview.Fingerprint)}\","
            + $"\"workerId\":\"{Escape(preview.WorkerId)}\","
            + $"\"samAccountName\":{ToJsonString(preview.SamAccountName)},"
            + $"\"previousRunId\":{ToJsonString(preview.PreviousRunId)},"
            + $"\"preview\":{SerializePreview(preview)}"
            + "}");

        var bucket = ResolvePersistedBucket(preview);
        var runRecord = new RunRecord(
            RunId: preview.RunId ?? throw new InvalidOperationException("Preview run id is required."),
            Path: preview.ReportPath,
            ArtifactType: "WorkerPreview",
            ConfigPath: null,
            MappingConfigPath: null,
            Mode: "Preview",
            DryRun: true,
            Status: preview.Status ?? "Planned",
            StartedAt: startedAt,
            CompletedAt: startedAt,
            DurationSeconds: 0,
            Creates: bucket == "creates" ? 1 : 0,
            Updates: bucket == "updates" ? 1 : 0,
            Enables: bucket == "enables" ? 1 : 0,
            Disables: bucket == "disables" ? 1 : 0,
            GraveyardMoves: bucket == "graveyardMoves" ? 1 : 0,
            Deletions: bucket == "deletions" ? 1 : 0,
            Quarantined: bucket == "quarantined" ? 1 : 0,
            Conflicts: bucket == "conflicts" ? 1 : 0,
            GuardrailFailures: bucket == "guardrailFailures" ? 1 : 0,
            ManualReview: bucket == "manualReview" ? 1 : 0,
            Unchanged: preview.DiffRows.All(row => !row.Changed) ? 1 : 0,
            Report: report);

        var entryItem = ParseJson(
            "{"
            + $"\"workerId\":\"{Escape(preview.WorkerId)}\","
            + $"\"samAccountName\":{ToJsonString(preview.SamAccountName)},"
            + $"\"targetOu\":{ToJsonString(preview.TargetOu)},"
            + $"\"emplStatus\":{ToJsonString(ResolveSourceAttribute(preview.SourceAttributes, "emplStatus"))},"
            + $"\"endDate\":{ToJsonString(ResolveSourceAttribute(preview.SourceAttributes, "endDate"))},"
            + $"\"managerDistinguishedName\":{ToJsonString(preview.ManagerDistinguishedName)},"
            + $"\"reviewCaseType\":{ToJsonString(preview.ReviewCaseType)},"
            + $"\"reason\":{ToJsonString(preview.Reason)},"
            + $"\"matchedExistingUser\":{ToJsonBoolean(preview.MatchedExistingUser ?? false)},"
            + $"\"proposedEnable\":{ToJsonNullableBoolean(preview.ProposedEnable)},"
            + $"\"currentEnabled\":{ToJsonNullableBoolean(preview.CurrentEnabled)},"
            + $"\"currentOu\":{ToJsonString(preview.OperationSummary?.FromOu)},"
            + $"\"targetOu\":{ToJsonString(preview.TargetOu)},"
            + "\"operations\":["
            + string.Join(",", preview.Entries
                .SelectMany(entry => entry.Item.TryGetProperty("operations", out var operations) && operations.ValueKind == JsonValueKind.Array
                    ? operations.EnumerateArray().Select(operation => operation.GetRawText())
                    : []))
            + "],"
            + "\"changedAttributeDetails\":["
            + string.Join(",", preview.DiffRows
                .Where(row => row.Changed)
                .Select(row => "{"
                    + $"\"targetAttribute\":\"{Escape(row.Attribute)}\","
                    + $"\"sourceField\":{ToJsonString(row.Source)},"
                    + $"\"currentAdValue\":{ToJsonString(row.Before == "(unset)" ? null : row.Before)},"
                    + $"\"proposedValue\":{ToJsonString(row.After == "(unset)" ? null : row.After)}"
                    + "}"))
            + "]"
            + "}");

        await runRepository.SaveRunAsync(runRecord, cancellationToken);
        await runRepository.ReplaceRunEntriesAsync(
            preview.RunId!,
            [
                new RunEntryRecord(
                    EntryId: $"{preview.RunId}:{preview.WorkerId}:0",
                    RunId: preview.RunId,
                    Bucket: bucket,
                    BucketIndex: 0,
                    WorkerId: preview.WorkerId,
                    SamAccountName: preview.SamAccountName,
                    Reason: preview.Reason,
                    ReviewCategory: preview.ReviewCategory,
                    ReviewCaseType: preview.ReviewCaseType,
                    StartedAt: startedAt,
                    Item: entryItem)
            ],
            cancellationToken);
    }

    private static WorkerPreviewResult BuildPreview(
        PlannedWorkerAction plan,
        string? logPath,
        IReadOnlyList<AttributeMapping> mappings)
    {
        var diffRows = plan.AttributeChanges
            .Select(change => new DiffRow(change.Attribute, change.Source, change.Before, change.After, change.Changed))
            .ToList();
        if (plan.CurrentEnabled.HasValue &&
            plan.TargetEnabled != plan.CurrentEnabled.Value &&
            diffRows.All(row => !string.Equals(row.Attribute, "enabled", StringComparison.OrdinalIgnoreCase)))
        {
            diffRows.Add(new DiffRow(
                Attribute: "enabled",
                Source: null,
                Before: ToInlineBoolean(plan.CurrentEnabled.Value),
                After: ToInlineBoolean(plan.TargetEnabled),
                Changed: true));
        }

        var finalizedDiffRows = diffRows.ToArray();
        var sourceAttributes = plan.Worker.Attributes
            .Where(attribute => !string.IsNullOrWhiteSpace(attribute.Value))
            .Select(attribute => new SourceAttributeRow(attribute.Key, attribute.Value!))
            .OrderBy(attribute => IsPathLikeAttribute(attribute.Attribute) ? 1 : 0)
            .ThenBy(attribute => attribute.Attribute, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var usedSources = BuildUsedSourceAttributes(sourceAttributes, finalizedDiffRows);
        var unusedSources = sourceAttributes
            .Where(attribute => usedSources.All(used => !string.Equals(used.Attribute, attribute.Attribute, StringComparison.OrdinalIgnoreCase)))
            .ToArray();
        var missingSources = plan.MissingSourceAttributes;

        var item = ParseJson(
            "{"
            + $"\"workerId\":\"{Escape(plan.Worker.WorkerId)}\","
            + $"\"samAccountName\":\"{Escape(plan.Identity.SamAccountName)}\","
            + $"\"targetOu\":\"{Escape(plan.Worker.TargetOu)}\","
            + $"\"emplStatus\":{ToJsonString(ResolveSourceAttribute(plan.Worker.Attributes, "emplStatus"))},"
            + $"\"endDate\":{ToJsonString(ResolveSourceAttribute(plan.Worker.Attributes, "endDate"))},"
            + $"\"matchedExistingUser\":{ToJsonBoolean(plan.Identity.MatchedExistingUser)},"
            + "\"changedAttributeDetails\":["
            + string.Join(",", finalizedDiffRows
                .Where(row => row.Changed)
                .Select(row => "{"
                    + $"\"targetAttribute\":\"{Escape(row.Attribute)}\","
                    + $"\"sourceField\":{ToJsonString(row.Source)},"
                    + $"\"currentAdValue\":{ToJsonString(row.Before == "(unset)" ? null : row.Before)},"
                    + $"\"proposedValue\":{ToJsonString(row.After)}"
                    + "}"))
            + "]"
            + "}");

        return new WorkerPreviewResult(
            ReportPath: logPath,
            RunId: null,
            PreviousRunId: null,
            Fingerprint: string.Empty,
            Mode: "Preview",
            Status: "Planned",
            ErrorMessage: null,
            ArtifactType: "WorkerPreview",
            SuccessFactorsAuth: "NativeScaffold",
            WorkerId: plan.Worker.WorkerId,
            Buckets: [plan.Bucket],
            MatchedExistingUser: plan.Identity.MatchedExistingUser,
            ReviewCategory: plan.ReviewCategory,
            ReviewCaseType: plan.ReviewCaseType,
            Reason: plan.Reason,
            OperatorActionSummary: plan.Identity.OperatorActionSummary,
            SamAccountName: plan.Identity.SamAccountName,
            ManagerDistinguishedName: plan.ManagerDistinguishedName,
            TargetOu: plan.TargetOu,
            CurrentDistinguishedName: plan.DirectoryUser.DistinguishedName,
            CurrentEnabled: plan.CurrentEnabled,
            ProposedEnable: plan.TargetEnabled,
            OperationSummary: new OperationSummary(
                Action: DescribeOperation(plan),
                Effect: DescribeEffect(plan, finalizedDiffRows),
                TargetOu: plan.TargetOu,
                FromOu: plan.CurrentOu,
                ToOu: plan.TargetOu),
            DiffRows: finalizedDiffRows,
            SourceAttributes: sourceAttributes,
            UsedSourceAttributes: usedSources,
            UnusedSourceAttributes: unusedSources,
            MissingSourceAttributes: missingSources,
            Entries:
            [
                new WorkerPreviewEntry(
                    Bucket: plan.Bucket,
                    Item: AddOperations(item, plan.Operations))
            ]);
    }

    private static JsonElement ParseJson(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    private static string ResolvePersistedBucket(WorkerPreviewResult preview)
    {
        var bucket = preview.Buckets.FirstOrDefault() ?? "updates";
        return string.Equals(bucket, "updates", StringComparison.OrdinalIgnoreCase) &&
               preview.DiffRows.All(row => !row.Changed)
            ? "unchanged"
            : bucket;
    }

    private static string ToInlineBoolean(bool value) => value ? "true" : "false";

    private static string Escape(string value)
    {
        var serialized = JsonSerializer.Serialize(value);
        return serialized[1..^1];
    }

    private static string ToJsonString(string? value) => value is null ? "null" : $"\"{Escape(value)}\"";

    private static string ToJsonBoolean(bool value) => value ? "true" : "false";

    private static string ToJsonNullableBoolean(bool? value) => value.HasValue ? ToJsonBoolean(value.Value) : "null";

    private static string? ResolveSourceAttribute(IReadOnlyDictionary<string, string?> attributes, string key)
    {
        if (attributes.TryGetValue(key, out var value))
        {
            return value;
        }

        var normalized = SourceAttributePathNormalizer.Normalize(key);
        return attributes.TryGetValue(normalized, out value) ? value : null;
    }

    private static string? ResolveSourceAttribute(IReadOnlyList<SourceAttributeRow> sourceAttributes, string key)
    {
        return sourceAttributes
            .FirstOrDefault(attribute => string.Equals(attribute.Attribute, key, StringComparison.OrdinalIgnoreCase))
            ?.Value;
    }

    private static string SerializePreview(WorkerPreviewResult preview)
    {
        return JsonSerializer.Serialize(preview);
    }

    private static JsonElement AddOperations(JsonElement item, IReadOnlyList<DirectoryOperation> operations)
    {
        return ParseJson(
            "{"
            + $"\"workerId\":{item.GetProperty("workerId").GetRawText()},"
            + $"\"samAccountName\":{item.GetProperty("samAccountName").GetRawText()},"
            + $"\"targetOu\":{item.GetProperty("targetOu").GetRawText()},"
            + $"\"emplStatus\":{item.GetProperty("emplStatus").GetRawText()},"
            + $"\"endDate\":{item.GetProperty("endDate").GetRawText()},"
            + $"\"matchedExistingUser\":{item.GetProperty("matchedExistingUser").GetRawText()},"
            + "\"operations\":["
            + string.Join(",", operations.Select(operation =>
                "{"
                + $"\"kind\":\"{Escape(operation.Kind)}\","
                + $"\"targetOu\":{ToJsonString(operation.TargetOu)}"
                + "}"))
            + "],"
            + "\"changedAttributeDetails\":"
            + item.GetProperty("changedAttributeDetails").GetRawText()
            + "}");
    }

    private static string DescribeOperation(PlannedWorkerAction plan)
    {
        var sam = plan.Identity.SamAccountName;
        return plan.Bucket switch
        {
            "creates" => $"Create account {sam}",
            "updates" => $"Update account {sam}",
            "enables" => $"Activate account {sam}",
            "disables" => $"Disable account {sam}",
            "graveyardMoves" => $"Move account {sam} to graveyard",
            "unchanged" => $"No changes for {sam}",
            _ => plan.PrimaryAction
        };
    }

    private static string? DescribeEffect(PlannedWorkerAction plan, IReadOnlyList<DiffRow> diffRows)
    {
        var changedCount = diffRows.Count(row => row.Changed);
        var operationCount = plan.Operations.Count;
        if (plan.Bucket == "creates")
        {
            return plan.TargetEnabled
                ? "Create active account."
                : "Create disabled account in the prehire OU.";
        }

        if (operationCount == 0 && changedCount == 0)
        {
            return "No attribute or lifecycle changes.";
        }

        var parts = new List<string>();
        if (operationCount > 0)
        {
            parts.Add($"{operationCount} directory operation{(operationCount == 1 ? string.Empty : "s")}");
        }

        if (changedCount > 0)
        {
            parts.Add($"{changedCount} attribute change{(changedCount == 1 ? string.Empty : "s")}");
        }

        return string.Join(", ", parts) + ".";
    }

    private static IReadOnlyList<SourceAttributeRow> BuildUsedSourceAttributes(
        IReadOnlyList<SourceAttributeRow> sourceAttributes,
        IReadOnlyList<DiffRow> diffRows)
    {
        var usedKeys = diffRows
            .SelectMany(row => SplitSourceKeys(row.Source))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        return sourceAttributes
            .Where(attribute => usedKeys.Contains(attribute.Attribute))
            .ToArray();
    }

    private static IEnumerable<string> SplitSourceKeys(string? source)
    {
        if (string.IsNullOrWhiteSpace(source))
        {
            return [];
        }

        return source.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }

    private static bool IsPathLikeAttribute(string attribute)
    {
        return attribute.Contains('[', StringComparison.Ordinal) || attribute.Contains('.', StringComparison.Ordinal);
    }
}

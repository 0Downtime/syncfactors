using SyncFactors.Contracts;
using Microsoft.Extensions.Logging;
using System.Text.Json;

namespace SyncFactors.Domain;

public sealed class WorkerPreviewPlanner(
    IWorkerSource workerSource,
    IDirectoryGateway directoryGateway,
    IIdentityMatcher identityMatcher,
    IAttributeDiffService attributeDiffService,
    IAttributeMappingProvider attributeMappingProvider,
    IWorkerPreviewLogWriter previewLogWriter,
    IRunRepository runRepository,
    ILogger<WorkerPreviewPlanner> logger) : IWorkerPreviewPlanner
{
    public async Task<WorkerPreviewResult> PreviewAsync(string workerId, CancellationToken cancellationToken)
    {
        logger.LogInformation("Starting worker preview. WorkerId={WorkerId}", workerId);
        var startedAt = DateTimeOffset.UtcNow;
        var worker = await workerSource.GetWorkerAsync(workerId, cancellationToken);
        if (worker is null)
        {
            logger.LogWarning("Worker preview could not resolve worker. WorkerId={WorkerId}", workerId);
            throw new InvalidOperationException($"Worker {workerId} could not be resolved.");
        }

        var logPath = previewLogWriter.CreateLogPath(workerId, startedAt);

        var directoryUser = await directoryGateway.FindByWorkerAsync(worker, cancellationToken)
            ?? new DirectoryUserSnapshot(
                SamAccountName: null,
                DistinguishedName: null,
                Enabled: null,
                DisplayName: null,
                Attributes: new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase));
        var managerId = worker.Attributes.TryGetValue("managerId", out var resolvedManagerId) ? resolvedManagerId : null;
        var managerDistinguishedName = managerId is null
            ? null
            : await directoryGateway.ResolveManagerDistinguishedNameAsync(managerId, cancellationToken);

        var identity = identityMatcher.Match(worker, directoryUser);
        var proposedEmailLocalPart = await directoryGateway.ResolveAvailableEmailLocalPartAsync(worker, cancellationToken);
        var proposedEmailAddress = DirectoryIdentityFormatter.BuildEmailAddress(proposedEmailLocalPart);

        var attributeChanges = await attributeDiffService.BuildDiffAsync(worker, directoryUser, proposedEmailAddress, logPath, cancellationToken);
        logger.LogInformation(
            "Worker preview completed planning. WorkerId={WorkerId} Bucket={Bucket} MatchedExistingUser={MatchedExistingUser} DiffCount={DiffCount}",
            worker.WorkerId,
            identity.Bucket,
            identity.MatchedExistingUser,
            attributeChanges.Count(change => change.Changed));
        var preview = BuildPreview(
            worker,
            directoryUser,
            identity,
            attributeChanges,
            managerDistinguishedName,
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

        var bucket = preview.Buckets.FirstOrDefault() ?? "updates";
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
            + $"\"managerDistinguishedName\":{ToJsonString(preview.ManagerDistinguishedName)},"
            + $"\"reviewCaseType\":{ToJsonString(preview.ReviewCaseType)},"
            + $"\"reason\":{ToJsonString(preview.Reason)},"
            + $"\"matchedExistingUser\":{ToJsonBoolean(preview.MatchedExistingUser ?? false)},"
            + $"\"proposedEnable\":{ToJsonNullableBoolean(preview.ProposedEnable)},"
            + $"\"currentEnabled\":{ToJsonNullableBoolean(preview.CurrentEnabled)},"
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
        WorkerSnapshot worker,
        DirectoryUserSnapshot directoryUser,
        IdentityMatchResult identity,
        IReadOnlyList<AttributeChange> attributeChanges,
        string? managerDistinguishedName,
        string? logPath,
        IReadOnlyList<AttributeMapping> mappings)
    {
        var diffRows = attributeChanges
            .Select(change => new DiffRow(change.Attribute, change.Source, change.Before, change.After, change.Changed))
            .ToArray();
        var sourceAttributes = worker.Attributes
            .Where(attribute => !string.IsNullOrWhiteSpace(attribute.Value))
            .Select(attribute => new SourceAttributeRow(attribute.Key, attribute.Value!))
            .OrderBy(attribute => IsPathLikeAttribute(attribute.Attribute) ? 1 : 0)
            .ThenBy(attribute => attribute.Attribute, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var usedSources = BuildUsedSourceAttributes(sourceAttributes, diffRows);
        var unusedSources = sourceAttributes
            .Where(attribute => usedSources.All(used => !string.Equals(used.Attribute, attribute.Attribute, StringComparison.OrdinalIgnoreCase)))
            .ToArray();
        var missingSources = BuildMissingSourceAttributes(worker.Attributes, mappings);

        var item = ParseJson(
            "{"
            + $"\"workerId\":\"{Escape(worker.WorkerId)}\","
            + $"\"samAccountName\":\"{Escape(identity.SamAccountName)}\","
            + $"\"targetOu\":\"{Escape(worker.TargetOu)}\","
            + $"\"matchedExistingUser\":{ToJsonBoolean(identity.MatchedExistingUser)},"
            + "\"changedAttributeDetails\":["
            + string.Join(",", diffRows
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
            WorkerId: worker.WorkerId,
            Buckets: [identity.Bucket],
            MatchedExistingUser: identity.MatchedExistingUser,
            ReviewCategory: null,
            ReviewCaseType: null,
            Reason: identity.Reason,
            OperatorActionSummary: identity.OperatorActionSummary,
            SamAccountName: identity.SamAccountName,
            ManagerDistinguishedName: managerDistinguishedName,
            TargetOu: worker.TargetOu,
            CurrentDistinguishedName: directoryUser.DistinguishedName,
            CurrentEnabled: directoryUser.Enabled,
            ProposedEnable: directoryUser.Enabled ?? true,
            OperationSummary: new OperationSummary(
                Action: identity.Bucket == "creates" ? $"Create account {identity.SamAccountName}" : $"Update attributes for {identity.SamAccountName}",
                Effect: identity.Bucket == "creates" ? null : $"{diffRows.Count(row => row.Changed)} attribute change.",
                TargetOu: worker.TargetOu,
                FromOu: null,
                ToOu: worker.TargetOu),
            DiffRows: diffRows,
            SourceAttributes: sourceAttributes,
            UsedSourceAttributes: usedSources,
            UnusedSourceAttributes: unusedSources,
            MissingSourceAttributes: missingSources,
            Entries:
            [
                new WorkerPreviewEntry(
                    Bucket: identity.Bucket,
                    Item: item)
            ]);
    }

    private static JsonElement ParseJson(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    private static string Escape(string value)
    {
        return value.Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("\"", "\\\"", StringComparison.Ordinal);
    }

    private static string ToJsonString(string? value) => value is null ? "null" : $"\"{Escape(value)}\"";

    private static string ToJsonBoolean(bool value) => value ? "true" : "false";

    private static string ToJsonNullableBoolean(bool? value) => value.HasValue ? ToJsonBoolean(value.Value) : "null";

    private static string SerializePreview(WorkerPreviewResult preview)
    {
        return JsonSerializer.Serialize(preview);
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

    private static IReadOnlyList<MissingSourceAttributeRow> BuildMissingSourceAttributes(
        IReadOnlyDictionary<string, string?> attributes,
        IReadOnlyList<AttributeMapping> mappings)
    {
        var missing = new List<MissingSourceAttributeRow>();
        foreach (var mapping in mappings.Where(mapping => mapping.Required))
        {
            if (TryResolveAttribute(attributes, mapping.Source, out var value) &&
                !string.IsNullOrWhiteSpace(value))
            {
                continue;
            }

            missing.Add(new MissingSourceAttributeRow(mapping.Source, $"Required mapping for {mapping.Target} has no value."));
        }

        return missing
            .DistinctBy(row => row.Attribute, StringComparer.OrdinalIgnoreCase)
            .OrderBy(row => row.Attribute, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static bool TryResolveAttribute(
        IReadOnlyDictionary<string, string?> attributes,
        string source,
        out string? value)
    {
        foreach (var key in SplitSourceKeys(source))
        {
            if (attributes.TryGetValue(key, out value))
            {
                return true;
            }
        }

        value = null;
        return false;
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

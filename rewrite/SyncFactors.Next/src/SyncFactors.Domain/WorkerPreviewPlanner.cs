using SyncFactors.Contracts;
using Microsoft.Extensions.Logging;
using System.Text.Json;

namespace SyncFactors.Domain;

public sealed class WorkerPreviewPlanner(
    IWorkerSource workerSource,
    IDirectoryGateway directoryGateway,
    IIdentityMatcher identityMatcher,
    IAttributeDiffService attributeDiffService,
    ILogger<WorkerPreviewPlanner> logger) : IWorkerPreviewPlanner
{
    public async Task<WorkerPreviewResult> PreviewAsync(string workerId, CancellationToken cancellationToken)
    {
        logger.LogInformation("Starting worker preview. WorkerId={WorkerId}", workerId);
        var worker = await workerSource.GetWorkerAsync(workerId, cancellationToken);
        if (worker is null)
        {
            logger.LogWarning("Worker preview could not resolve worker. WorkerId={WorkerId}", workerId);
            throw new InvalidOperationException($"Worker {workerId} could not be resolved.");
        }

        var directoryUser = await directoryGateway.FindByWorkerAsync(worker, cancellationToken)
            ?? new DirectoryUserSnapshot(
                SamAccountName: null,
                DistinguishedName: null,
                Enabled: null,
                DisplayName: null,
                Attributes: new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase));

        var identity = identityMatcher.Match(worker, directoryUser);

        var attributeChanges = attributeDiffService.BuildDiff(worker, directoryUser);
        logger.LogInformation(
            "Worker preview completed planning. WorkerId={WorkerId} Bucket={Bucket} MatchedExistingUser={MatchedExistingUser} DiffCount={DiffCount}",
            worker.WorkerId,
            identity.Bucket,
            identity.MatchedExistingUser,
            attributeChanges.Count(change => change.Changed));
        return BuildPreview(worker, directoryUser, identity, attributeChanges);
    }

    private static WorkerPreviewResult BuildPreview(
        WorkerSnapshot worker,
        DirectoryUserSnapshot directoryUser,
        IdentityMatchResult identity,
        IReadOnlyList<AttributeChange> attributeChanges)
    {
        var diffRows = attributeChanges
            .Select(change => new DiffRow(change.Attribute, change.Source, change.Before, change.After, change.Changed))
            .ToArray();

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
            ReportPath: null,
            RunId: null,
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
}

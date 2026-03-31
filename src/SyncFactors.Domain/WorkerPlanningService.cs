using System.Text.Json;
using Microsoft.Extensions.Logging;
using SyncFactors.Contracts;

namespace SyncFactors.Domain;

public sealed class WorkerPlanningService(
    IDirectoryGateway directoryGateway,
    IIdentityMatcher identityMatcher,
    IAttributeDiffService attributeDiffService,
    IAttributeMappingProvider attributeMappingProvider,
    ILogger<WorkerPlanningService> logger) : IWorkerPlanningService
{
    public async Task<PlannedWorkerAction> PlanAsync(WorkerSnapshot worker, string? logPath, CancellationToken cancellationToken)
    {
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
        var missingSourceAttributes = BuildMissingSourceAttributes(worker.Attributes, attributeMappingProvider.GetEnabledMappings());

        var bucket = identity.Bucket;
        string? reviewCaseType = null;
        string? reviewCategory = null;
        string? reason = identity.Reason;

        if (missingSourceAttributes.Count > 0)
        {
            bucket = "manualReview";
            reviewCategory = "RequiredMapping";
            reviewCaseType = "MissingRequiredSourceAttribute";
            reason = missingSourceAttributes[0].Reason;
        }
        else if (!string.IsNullOrWhiteSpace(managerId) && string.IsNullOrWhiteSpace(managerDistinguishedName))
        {
            bucket = "manualReview";
            reviewCategory = "Manager";
            reviewCaseType = "ManagerResolutionFailed";
            reason = $"Manager {managerId} could not be resolved in Active Directory.";
        }

        var canAutoApply =
            string.Equals(bucket, "creates", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(bucket, "updates", StringComparison.OrdinalIgnoreCase);

        logger.LogInformation(
            "Planned worker action. WorkerId={WorkerId} Bucket={Bucket} AutoApply={CanAutoApply} MissingRequiredCount={MissingRequiredCount}",
            worker.WorkerId,
            bucket,
            canAutoApply,
            missingSourceAttributes.Count);

        return new PlannedWorkerAction(
            Worker: worker,
            DirectoryUser: directoryUser,
            Identity: identity,
            ManagerDistinguishedName: managerDistinguishedName,
            ProposedEmailAddress: proposedEmailAddress,
            AttributeChanges: attributeChanges,
            MissingSourceAttributes: missingSourceAttributes,
            Bucket: bucket,
            ReviewCategory: reviewCategory,
            ReviewCaseType: reviewCaseType,
            Reason: reason,
            CanAutoApply: canAutoApply);
    }

    internal static IReadOnlyList<MissingSourceAttributeRow> BuildMissingSourceAttributes(
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
}

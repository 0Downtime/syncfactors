using System.Text.Json;
using Microsoft.Extensions.Logging;
using SyncFactors.Contracts;

namespace SyncFactors.Domain;

public sealed class WorkerPlanningService(
    IDirectoryGateway directoryGateway,
    IIdentityMatcher identityMatcher,
    ILifecyclePolicy lifecyclePolicy,
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
        string? managerDistinguishedName = null;
        if (!string.IsNullOrWhiteSpace(managerId))
        {
            try
            {
                managerDistinguishedName = await directoryGateway.ResolveManagerDistinguishedNameAsync(managerId, cancellationToken);
            }
            catch (Exception ex)
            {
                logger.LogWarning(
                    ex,
                    "Manager lookup failed during worker planning. WorkerId={WorkerId} ManagerId={ManagerId}",
                    worker.WorkerId,
                    managerId);
            }
        }

        var identity = identityMatcher.Match(worker, directoryUser);
        var lifecycle = lifecyclePolicy.Evaluate(worker, directoryUser);
        var suppressInactiveCreateValidation = ShouldSuppressInactiveCreateValidation(lifecycle, directoryUser);
        var proposedEmailAddress = string.Empty;
        var attributeChanges = new List<AttributeChange>();
        IReadOnlyList<MissingSourceAttributeRow> missingSourceAttributes = [];

        if (!suppressInactiveCreateValidation)
        {
            proposedEmailAddress = identity.MatchedExistingUser
                ? directoryUser.Attributes.TryGetValue("UserPrincipalName", out var existingUserPrincipalName) && !string.IsNullOrWhiteSpace(existingUserPrincipalName)
                    ? existingUserPrincipalName
                    : directoryUser.Attributes.TryGetValue("mail", out var existingMail) && !string.IsNullOrWhiteSpace(existingMail)
                        ? existingMail
                        : DirectoryIdentityFormatter.BuildEmailAddress(
                            await directoryGateway.ResolveAvailableEmailLocalPartAsync(worker, isCreate: false, cancellationToken))
                : DirectoryIdentityFormatter.BuildEmailAddress(
                    await directoryGateway.ResolveAvailableEmailLocalPartAsync(worker, isCreate: true, cancellationToken));
            attributeChanges = (await attributeDiffService.BuildDiffAsync(worker, directoryUser, proposedEmailAddress, logPath, cancellationToken))
                .ToList();
            UpsertManagerAttributeChange(attributeChanges, directoryUser, managerDistinguishedName);
            missingSourceAttributes = BuildMissingSourceAttributes(
                worker.Attributes,
                attributeMappingProvider.GetEnabledMappings(),
                proposedEmailAddress);
        }

        var currentOu = DirectoryDistinguishedName.GetParentOu(directoryUser.DistinguishedName);
        var bucket = lifecycle.Bucket;
        string? reviewCaseType = null;
        string? reviewCategory = null;
        string? reason = lifecycle.Reason ?? identity.Reason;

        if (missingSourceAttributes.Count > 0)
        {
            bucket = "manualReview";
            reviewCategory = "RequiredMapping";
            reviewCaseType = "MissingRequiredSourceAttribute";
            reason = missingSourceAttributes[0].Reason;
        }
        var targetEnabled = lifecycle.TargetEnabled;
        var operations = BuildOperations(bucket, directoryUser, lifecycle.TargetOu, targetEnabled, attributeChanges);
        bucket = ResolveBucket(bucket, directoryUser, lifecycle.TargetOu, targetEnabled, attributeChanges);
        var primaryAction = ResolvePrimaryAction(bucket, operations);
        var canAutoApply = operations.Count > 0;

        logger.LogInformation(
            "Planned worker action. Bucket={Bucket} AutoApply={CanAutoApply} MissingRequiredCount={MissingRequiredCount}",
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
            CurrentOu: currentOu,
            TargetOu: lifecycle.TargetOu,
            CurrentEnabled: directoryUser.Enabled,
            TargetEnabled: targetEnabled,
            PrimaryAction: primaryAction,
            Operations: operations,
            ReviewCategory: reviewCategory,
            ReviewCaseType: reviewCaseType,
            Reason: reason,
            CanAutoApply: canAutoApply);
    }

    private static bool ShouldSuppressInactiveCreateValidation(
        LifecycleDecision lifecycle,
        DirectoryUserSnapshot directoryUser)
    {
        return string.Equals(lifecycle.Bucket, "unchanged", StringComparison.OrdinalIgnoreCase) &&
               string.IsNullOrWhiteSpace(directoryUser.SamAccountName);
    }

    private static void UpsertManagerAttributeChange(
        IList<AttributeChange> attributeChanges,
        DirectoryUserSnapshot directoryUser,
        string? proposedManagerDistinguishedName)
    {
        var currentManagerDistinguishedName = directoryUser.Attributes.TryGetValue("manager", out var managerValue)
            ? managerValue
            : null;
        var normalizedCurrentManagerDistinguishedName = string.IsNullOrWhiteSpace(currentManagerDistinguishedName)
            ? null
            : currentManagerDistinguishedName;
        var normalizedProposedManagerDistinguishedName = string.IsNullOrWhiteSpace(proposedManagerDistinguishedName)
            ? null
            : proposedManagerDistinguishedName;

        if (normalizedCurrentManagerDistinguishedName is null && normalizedProposedManagerDistinguishedName is null)
        {
            return;
        }

        var before = normalizedCurrentManagerDistinguishedName ?? "(unset)";
        var after = normalizedProposedManagerDistinguishedName ?? before;
        var changed = normalizedProposedManagerDistinguishedName is not null
            && !string.Equals(normalizedCurrentManagerDistinguishedName, normalizedProposedManagerDistinguishedName, StringComparison.OrdinalIgnoreCase);
        var replacement = new AttributeChange("manager", "managerId", before, after, changed);

        for (var index = 0; index < attributeChanges.Count; index++)
        {
            if (string.Equals(attributeChanges[index].Attribute, "manager", StringComparison.OrdinalIgnoreCase))
            {
                attributeChanges[index] = replacement;
                return;
            }
        }

        attributeChanges.Add(replacement);
    }

    private static IReadOnlyList<DirectoryOperation> BuildOperations(
        string bucket,
        DirectoryUserSnapshot directoryUser,
        string targetOu,
        bool targetEnabled,
        IReadOnlyList<AttributeChange> attributeChanges)
    {
        var operations = new List<DirectoryOperation>();
        var hasExistingUser = !string.IsNullOrWhiteSpace(directoryUser.SamAccountName);
        var currentOu = DirectoryDistinguishedName.GetParentOu(directoryUser.DistinguishedName);
        var hasAttributeChanges = attributeChanges.Any(change => change.Changed);

        if (string.Equals(bucket, "manualReview", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(bucket, "quarantined", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(bucket, "unchanged", StringComparison.OrdinalIgnoreCase))
        {
            return operations;
        }

        if (!hasExistingUser)
        {
            operations.Add(new DirectoryOperation("CreateUser", targetOu));
            return operations;
        }

        if (!string.IsNullOrWhiteSpace(targetOu) &&
            !string.Equals(currentOu, targetOu, StringComparison.OrdinalIgnoreCase))
        {
            operations.Add(new DirectoryOperation("MoveUser", targetOu));
        }

        if (hasAttributeChanges)
        {
            operations.Add(new DirectoryOperation("UpdateUser"));
        }

        if (directoryUser.Enabled != targetEnabled)
        {
            operations.Add(new DirectoryOperation(targetEnabled ? "EnableUser" : "DisableUser"));
        }

        return operations;
    }

    private static string ResolvePrimaryAction(string bucket, IReadOnlyList<DirectoryOperation> operations)
    {
        if (operations.Count > 0)
        {
            return operations[0].Kind;
        }

        return bucket switch
        {
            "creates" => "CreateUser",
            "updates" => "UpdateUser",
            "enables" => "EnableUser",
            "disables" => "DisableUser",
            "graveyardMoves" => "MoveUser",
            _ => "NoOp"
        };
    }

    private static string ResolveBucket(
        string lifecycleBucket,
        DirectoryUserSnapshot directoryUser,
        string targetOu,
        bool targetEnabled,
        IReadOnlyList<AttributeChange> attributeChanges)
    {
        if (!string.Equals(lifecycleBucket, "enables", StringComparison.OrdinalIgnoreCase))
        {
            return lifecycleBucket;
        }

        if (attributeChanges.Any(change => change.Changed))
        {
            return "updates";
        }

        var currentOu = DirectoryDistinguishedName.GetParentOu(directoryUser.DistinguishedName);
        if (directoryUser.Enabled != targetEnabled)
        {
            return "enables";
        }

        return !string.IsNullOrWhiteSpace(targetOu) &&
               !string.Equals(currentOu, targetOu, StringComparison.OrdinalIgnoreCase)
            ? "enables"
            : "updates";
    }

    internal static IReadOnlyList<MissingSourceAttributeRow> BuildMissingSourceAttributes(
        IReadOnlyDictionary<string, string?> attributes,
        IReadOnlyList<AttributeMapping> mappings,
        string? proposedEmailAddress = null)
    {
        var missing = new List<MissingSourceAttributeRow>();
        foreach (var mapping in mappings.Where(mapping => mapping.Required))
        {
            if (TargetCanUseResolvedEmail(mapping.Target, proposedEmailAddress))
            {
                continue;
            }

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

    private static bool TargetCanUseResolvedEmail(string target, string? proposedEmailAddress)
    {
        return target is "UserPrincipalName" or "mail"
            && !string.IsNullOrWhiteSpace(proposedEmailAddress);
    }

    private static bool TryResolveAttribute(
        IReadOnlyDictionary<string, string?> attributes,
        string source,
        out string? value)
    {
        if (AttributeDiffService.TryParseConcatSource(source, out var concatKeys))
        {
            var parts = new List<string>();
            foreach (var key in concatKeys)
            {
                if (!TryResolveAttribute(attributes, key, out var part) || string.IsNullOrWhiteSpace(part))
                {
                    value = null;
                    return false;
                }

                parts.Add(part.Trim());
            }

            value = string.Join(' ', parts);
            return true;
        }

        foreach (var key in SplitSourceKeys(source))
        {
            if (attributes.TryGetValue(key, out value))
            {
                return true;
            }

            var normalizedKey = SourceAttributePathNormalizer.Normalize(key);
            if (!string.Equals(normalizedKey, key, StringComparison.OrdinalIgnoreCase) &&
                attributes.TryGetValue(normalizedKey, out value))
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

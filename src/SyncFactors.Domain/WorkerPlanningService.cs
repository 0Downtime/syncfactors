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
    ILogger<WorkerPlanningService> logger,
    IEmailAddressPolicy? emailAddressPolicy = null) : IWorkerPlanningService
{
    private readonly IEmailAddressPolicy _emailAddressPolicy = emailAddressPolicy ?? new DefaultEmailAddressPolicy();

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
                        : _emailAddressPolicy.BuildEmailAddress(
                            await directoryGateway.ResolveAvailableEmailLocalPartAsync(worker, isCreate: false, directoryUser, cancellationToken))
                : _emailAddressPolicy.BuildEmailAddress(
                    await directoryGateway.ResolveAvailableEmailLocalPartAsync(worker, isCreate: true, directoryUser, cancellationToken));
            attributeChanges = NormalizeAttributeChanges(
                await attributeDiffService.BuildDiffAsync(worker, directoryUser, proposedEmailAddress, logPath, cancellationToken))
                .ToList();
            UpsertManagerAttributeChange(attributeChanges, directoryUser, managerDistinguishedName);
            missingSourceAttributes = BuildMissingSourceAttributes(
                worker,
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
        bucket = ResolveBucket(bucket, directoryUser, lifecycle.TargetOu, targetEnabled, attributeChanges, operations);
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
        IReadOnlyList<AttributeChange> attributeChanges,
        IReadOnlyList<DirectoryOperation> operations)
    {
        if (operations.Count == 0)
        {
            return string.Equals(lifecycleBucket, "manualReview", StringComparison.OrdinalIgnoreCase)
                ? lifecycleBucket
                : "unchanged";
        }

        if (!string.Equals(lifecycleBucket, "enables", StringComparison.OrdinalIgnoreCase))
        {
            if (string.Equals(lifecycleBucket, "disables", StringComparison.OrdinalIgnoreCase) &&
                operations.All(operation => !string.Equals(operation.Kind, "DisableUser", StringComparison.OrdinalIgnoreCase)))
            {
                return "updates";
            }

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

    private static IReadOnlyList<AttributeChange> NormalizeAttributeChanges(IReadOnlyList<AttributeChange> attributeChanges)
    {
        return attributeChanges
            .Select(change =>
            {
                var normalizedAfter = string.Equals(change.After, "(unset)", StringComparison.Ordinal)
                    ? change.After
                    : ActiveDirectoryAttributeConstraints.NormalizeValue(change.Attribute, change.After) ?? change.After;
                return change with
                {
                    After = normalizedAfter,
                    Changed = !string.Equals(change.Before, normalizedAfter, StringComparison.Ordinal)
                };
            })
            .ToArray();
    }

    internal static IReadOnlyList<MissingSourceAttributeRow> BuildMissingSourceAttributes(
        WorkerSnapshot worker,
        IReadOnlyList<AttributeMapping> mappings,
        string? proposedEmailAddress = null)
    {
        var missing = new List<MissingSourceAttributeRow>();
        foreach (var mapping in mappings.Where(mapping => mapping.Required))
        {
            var value = SourceValueResolver.ResolveSourceValue(worker, mapping.Source, mapping.Target, proposedEmailAddress);
            if (!string.IsNullOrWhiteSpace(value))
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
}

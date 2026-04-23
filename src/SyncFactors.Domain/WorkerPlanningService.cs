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
        var directoryUser = new DirectoryUserSnapshot(
            SamAccountName: null,
            DistinguishedName: null,
            Enabled: null,
            DisplayName: null,
            Attributes: new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase));
        var identityReview = default((string ReviewCategory, string ReviewCaseType, string Reason)?);
        var sourceReview = ResolveSourceReview(worker);
        try
        {
            directoryUser = await directoryGateway.FindByWorkerAsync(worker, cancellationToken) ?? directoryUser;
        }
        catch (AmbiguousDirectoryIdentityException ex)
        {
            logger.LogWarning(ex, "Worker identity lookup is ambiguous. WorkerId={WorkerId}", worker.WorkerId);
            identityReview = ("DirectoryIdentity", "AmbiguousWorkerIdentity", ex.Message);
        }

        var managerId = worker.Attributes.TryGetValue("managerId", out var resolvedManagerId) ? resolvedManagerId : null;
        string? managerDistinguishedName = null;
        if (!string.IsNullOrWhiteSpace(managerId))
        {
            try
            {
                managerDistinguishedName = await directoryGateway.ResolveManagerDistinguishedNameAsync(managerId, cancellationToken);
            }
            catch (AmbiguousDirectoryIdentityException ex)
            {
                logger.LogWarning(
                    ex,
                    "Manager identity lookup is ambiguous during worker planning. WorkerId={WorkerId} ManagerId={ManagerId}",
                    worker.WorkerId,
                    managerId);
                identityReview ??= ("DirectoryIdentity", "AmbiguousManagerIdentity", ex.Message);
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
        var hasAmbiguousWorkerIdentity = string.Equals(identityReview?.ReviewCaseType, "AmbiguousWorkerIdentity", StringComparison.Ordinal);
        var hasSourceReviewBlock = sourceReview is not null;

        if (!suppressInactiveCreateValidation && !hasAmbiguousWorkerIdentity && !hasSourceReviewBlock)
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

        if (identityReview is not null)
        {
            bucket = "manualReview";
            reviewCategory = identityReview.Value.ReviewCategory;
            reviewCaseType = identityReview.Value.ReviewCaseType;
            reason = identityReview.Value.Reason;
        }
        else if (sourceReview is not null)
        {
            bucket = "manualReview";
            reviewCategory = sourceReview.Value.ReviewCategory;
            reviewCaseType = sourceReview.Value.ReviewCaseType;
            reason = sourceReview.Value.Reason;
        }

        if (missingSourceAttributes.Count > 0)
        {
            bucket = "unchanged";
            reviewCategory = null;
            reviewCaseType = null;
            reason = missingSourceAttributes[0].Reason;
        }
        var targetEnabled = lifecycle.TargetEnabled;
        var operations = BuildOperations(bucket, directoryUser, lifecycle.TargetOu, targetEnabled, attributeChanges);
        bucket = ResolveBucket(bucket, operations);
        var primaryAction = ResolvePrimaryAction(bucket, operations);
        var canAutoApply = operations.Count > 0;
        var decisionSteps = BuildDecisionSteps(
            worker,
            directoryUser,
            managerId,
            managerDistinguishedName,
            identityReview,
            sourceReview,
            identity,
            lifecycle,
            suppressInactiveCreateValidation,
            hasAmbiguousWorkerIdentity,
            hasSourceReviewBlock,
            proposedEmailAddress,
            attributeChanges,
            missingSourceAttributes,
            bucket,
            operations,
            reason,
            canAutoApply);

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
            CanAutoApply: canAutoApply,
            DecisionSteps: decisionSteps);
    }

    private static (string ReviewCategory, string ReviewCaseType, string Reason)? ResolveSourceReview(WorkerSnapshot worker)
    {
        if (!worker.Attributes.TryGetValue("_syncfactors.reviewCaseType", out var reviewCaseType) ||
            string.IsNullOrWhiteSpace(reviewCaseType))
        {
            return null;
        }

        var reviewCategory = worker.Attributes.TryGetValue("_syncfactors.reviewCategory", out var resolvedCategory) &&
                             !string.IsNullOrWhiteSpace(resolvedCategory)
            ? resolvedCategory
            : "SourceData";
        var reason = worker.Attributes.TryGetValue("_syncfactors.reviewReason", out var resolvedReason) &&
                     !string.IsNullOrWhiteSpace(resolvedReason)
            ? resolvedReason
            : "Worker source data was ambiguous.";

        return (reviewCategory, reviewCaseType, reason);
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
        IReadOnlyList<DirectoryOperation> operations)
    {
        if (operations.Count == 0)
        {
            return string.Equals(lifecycleBucket, "manualReview", StringComparison.OrdinalIgnoreCase)
                ? lifecycleBucket
                : "unchanged";
        }

        if (string.Equals(lifecycleBucket, "creates", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(lifecycleBucket, "graveyardMoves", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(lifecycleBucket, "deletions", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(lifecycleBucket, "quarantined", StringComparison.OrdinalIgnoreCase))
        {
            return lifecycleBucket;
        }

        if (string.Equals(lifecycleBucket, "enables", StringComparison.OrdinalIgnoreCase))
        {
            return "enables";
        }

        if (HasOperation(operations, "DisableUser"))
        {
            return "disables";
        }

        if (HasOperation(operations, "EnableUser"))
        {
            return "enables";
        }

        if (HasOperation(operations, "UpdateUser") ||
            HasOperation(operations, "MoveUser"))
        {
            return "updates";
        }

        return lifecycleBucket;
    }

    private static bool HasOperation(IReadOnlyList<DirectoryOperation> operations, string kind)
    {
        return operations.Any(operation => string.Equals(operation.Kind, kind, StringComparison.OrdinalIgnoreCase));
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

    private static IReadOnlyList<ProvisioningDecisionStep> BuildDecisionSteps(
        WorkerSnapshot worker,
        DirectoryUserSnapshot directoryUser,
        string? managerId,
        string? managerDistinguishedName,
        (string ReviewCategory, string ReviewCaseType, string Reason)? identityReview,
        (string ReviewCategory, string ReviewCaseType, string Reason)? sourceReview,
        IdentityMatchResult identity,
        LifecycleDecision lifecycle,
        bool suppressInactiveCreateValidation,
        bool hasAmbiguousWorkerIdentity,
        bool hasSourceReviewBlock,
        string? proposedEmailAddress,
        IReadOnlyList<AttributeChange> attributeChanges,
        IReadOnlyList<MissingSourceAttributeRow> missingSourceAttributes,
        string bucket,
        IReadOnlyList<DirectoryOperation> operations,
        string? reason,
        bool canAutoApply)
    {
        var steps = new List<ProvisioningDecisionStep>();
        var employmentStatus = ResolveWorkerAttribute(worker.Attributes, "emplStatus")
            ?? ResolveWorkerAttribute(worker.Attributes, "employeeStatus");
        var sourceSummary = $"Worker {worker.WorkerId} targets '{worker.TargetOu}'.";
        if (!string.IsNullOrWhiteSpace(employmentStatus))
        {
            sourceSummary += $" Employment status='{employmentStatus}'.";
        }

        sourceSummary += worker.IsPrehire
            ? " Worker is marked as prehire."
            : " Worker is not marked as prehire.";
        steps.Add(new ProvisioningDecisionStep("Source Worker", "Loaded", sourceSummary));

        if (string.Equals(identityReview?.ReviewCaseType, "AmbiguousWorkerIdentity", StringComparison.Ordinal))
        {
            var review = identityReview!.Value;
            steps.Add(new ProvisioningDecisionStep("Directory Identity", "Blocked", review.Reason, "warn"));
        }
        else if (sourceReview is not null)
        {
            steps.Add(new ProvisioningDecisionStep("Source Data", "Blocked", sourceReview.Value.Reason, "warn"));
        }
        else if (identity.MatchedExistingUser)
        {
            steps.Add(new ProvisioningDecisionStep(
                "Directory Identity",
                "Matched Existing User",
                $"Matched AD account '{identity.SamAccountName}', so planning continues against the existing object.",
                "good"));
        }
        else
        {
            steps.Add(new ProvisioningDecisionStep(
                "Directory Identity",
                "No Existing User",
                $"No AD account matched worker {worker.WorkerId}. Planning continues as a new account using SAM '{identity.SamAccountName}'."));
        }

        if (string.IsNullOrWhiteSpace(managerId))
        {
            steps.Add(new ProvisioningDecisionStep("Manager Resolution", "Skipped", "No source managerId was available for manager linking."));
        }
        else if (string.Equals(identityReview?.ReviewCaseType, "AmbiguousManagerIdentity", StringComparison.Ordinal))
        {
            var review = identityReview!.Value;
            steps.Add(new ProvisioningDecisionStep("Manager Resolution", "Blocked", review.Reason, "warn"));
        }
        else if (!string.IsNullOrWhiteSpace(managerDistinguishedName))
        {
            steps.Add(new ProvisioningDecisionStep(
                "Manager Resolution",
                "Resolved",
                $"managerId '{managerId}' resolved to '{managerDistinguishedName}'.",
                "good"));
        }
        else
        {
            steps.Add(new ProvisioningDecisionStep(
                "Manager Resolution",
                "Unresolved",
                $"managerId '{managerId}' did not resolve to an AD distinguished name. Planning continues without a manager update.",
                "warn"));
        }

        steps.Add(new ProvisioningDecisionStep(
            "Lifecycle Policy",
            DescribeBucket(lifecycle.Bucket),
            $"{lifecycle.Reason} Target OU='{lifecycle.TargetOu}'. Target enabled={FormatBooleanState(lifecycle.TargetEnabled)}."));

        if (suppressInactiveCreateValidation)
        {
            steps.Add(new ProvisioningDecisionStep(
                "Required Inputs",
                "Skipped",
                "Inactive worker has no existing AD account, so diff generation and required-mapping validation were skipped."));
        }
        else if (hasSourceReviewBlock)
        {
            steps.Add(new ProvisioningDecisionStep(
                "Required Inputs",
                "Skipped",
                "Required-mapping validation was skipped because source data requires manual review.",
                "warn"));
        }
        else if (hasAmbiguousWorkerIdentity)
        {
            steps.Add(new ProvisioningDecisionStep(
                "Required Inputs",
                "Skipped",
                "Required-mapping validation was skipped because worker identity resolution is ambiguous.",
                "warn"));
        }
        else if (missingSourceAttributes.Count > 0)
        {
            var missingList = string.Join(", ", missingSourceAttributes
                .Select(attribute => attribute.Attribute)
                .Distinct(StringComparer.OrdinalIgnoreCase));
            steps.Add(new ProvisioningDecisionStep(
                "Required Inputs",
                "Blocked",
                $"Automatic sync is blocked until required mapped source values are populated: {missingList}.",
                "warn"));
        }
        else
        {
            steps.Add(new ProvisioningDecisionStep(
                "Required Inputs",
                "Passed",
                "All required mapped source values needed for automatic sync are populated.",
                "good"));
        }

        if (suppressInactiveCreateValidation)
        {
            steps.Add(new ProvisioningDecisionStep(
                "Email Resolution",
                "Skipped",
                "No proposed email was computed because the worker will not be created or updated automatically."));
        }
        else if (hasSourceReviewBlock)
        {
            steps.Add(new ProvisioningDecisionStep(
                "Email Resolution",
                "Skipped",
                "No proposed email was computed because source data requires manual review.",
                "warn"));
        }
        else if (hasAmbiguousWorkerIdentity)
        {
            steps.Add(new ProvisioningDecisionStep(
                "Email Resolution",
                "Skipped",
                "No proposed email was computed because identity review blocked automatic planning.",
                "warn"));
        }
        else if (string.IsNullOrWhiteSpace(proposedEmailAddress))
        {
            steps.Add(new ProvisioningDecisionStep(
                "Email Resolution",
                "Unavailable",
                "Planner did not produce a proposed email address.",
                "warn"));
        }
        else if (identity.MatchedExistingUser)
        {
            steps.Add(new ProvisioningDecisionStep(
                "Email Resolution",
                "Resolved",
                $"Planning will use '{proposedEmailAddress}' while evaluating the matched AD account.",
                "good"));
        }
        else
        {
            steps.Add(new ProvisioningDecisionStep(
                "Email Resolution",
                "Resolved",
                $"Planning resolved '{proposedEmailAddress}' for the new AD account.",
                "good"));
        }

        if (suppressInactiveCreateValidation)
        {
            steps.Add(new ProvisioningDecisionStep(
                "Attribute Diff",
                "Skipped",
                "No mapped attribute diff was generated because automatic create/update planning was skipped."));
        }
        else if (hasSourceReviewBlock)
        {
            steps.Add(new ProvisioningDecisionStep(
                "Attribute Diff",
                "Skipped",
                "No mapped attribute diff was generated because source data requires manual review.",
                "warn"));
        }
        else if (hasAmbiguousWorkerIdentity)
        {
            steps.Add(new ProvisioningDecisionStep(
                "Attribute Diff",
                "Skipped",
                "No mapped attribute diff was generated because worker identity review blocked planning.",
                "warn"));
        }
        else
        {
            var changedCount = attributeChanges.Count(change => change.Changed);
            steps.Add(new ProvisioningDecisionStep(
                "Attribute Diff",
                changedCount > 0 ? "Changes Detected" : "No Changes",
                changedCount > 0
                    ? $"{changedCount} mapped attribute change{(changedCount == 1 ? string.Empty : "s")} were staged."
                    : "The worker already matches the mapped AD attribute values.",
                changedCount > 0 ? "good" : "neutral"));
        }

        steps.Add(new ProvisioningDecisionStep(
            "Directory Operations",
            operations.Count == 0 ? "None Planned" : $"{operations.Count} Planned",
            operations.Count == 0
                ? $"Final bucket '{DescribeBucket(bucket)}' produced no directory operations."
                : $"Final bucket '{DescribeBucket(bucket)}' will run {string.Join("; ", operations.Select(DescribeOperation))}.",
            operations.Count == 0 ? "neutral" : "good"));

        if (string.Equals(bucket, "manualReview", StringComparison.OrdinalIgnoreCase))
        {
            steps.Add(new ProvisioningDecisionStep(
                "Provisioning Decision",
                "No",
                $"Do not auto-provision. {reason ?? "Manual review is required before any AD write can occur."}",
                "warn"));
        }
        else if (!canAutoApply)
        {
            steps.Add(new ProvisioningDecisionStep(
                "Provisioning Decision",
                "No",
                string.Equals(bucket, "unchanged", StringComparison.OrdinalIgnoreCase)
                    ? "Do not provision because the worker already matches the target AD state."
                    : "Do not auto-provision because planning produced no directory operations."));
        }
        else
        {
            steps.Add(new ProvisioningDecisionStep(
                "Provisioning Decision",
                "Yes",
                identity.MatchedExistingUser
                    ? $"Yes. Real sync can update AD account '{identity.SamAccountName}' through {string.Join(", ", operations.Select(DescribeOperationShort))}."
                    : $"Yes. Real sync can provision new AD account '{identity.SamAccountName}' through {string.Join(", ", operations.Select(DescribeOperationShort))}.",
                "good"));
        }

        return steps;
    }

    private static string? ResolveWorkerAttribute(IReadOnlyDictionary<string, string?> attributes, string attribute)
    {
        foreach (var key in new[]
                 {
                     attribute,
                     SourceAttributePathNormalizer.Normalize(attribute),
                     $"employmentNav[0].jobInfoNav[0].{attribute}",
                     $"employmentNav/jobInfoNav/{attribute}"
                 })
        {
            if (attributes.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value))
            {
                return value;
            }
        }

        return null;
    }

    private static string DescribeBucket(string bucket) =>
        bucket switch
        {
            "creates" => "Create",
            "updates" => "Update",
            "enables" => "Enable",
            "disables" => "Disable",
            "graveyardMoves" => "Move To Graveyard",
            "manualReview" => "Manual Review",
            "unchanged" => "No Change",
            _ => bucket
        };

    private static string FormatBooleanState(bool value) => value ? "true" : "false";

    private static string DescribeOperation(DirectoryOperation operation)
    {
        var action = DescribeOperationShort(operation);
        return string.IsNullOrWhiteSpace(operation.TargetOu)
            ? action
            : $"{action} in '{operation.TargetOu}'";
    }

    private static string DescribeOperationShort(DirectoryOperation operation) =>
        operation.Kind switch
        {
            "CreateUser" => "create account",
            "MoveUser" => "move account",
            "UpdateUser" => "update attributes",
            "EnableUser" => "enable account",
            "DisableUser" => "disable account",
            _ => operation.Kind
        };

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

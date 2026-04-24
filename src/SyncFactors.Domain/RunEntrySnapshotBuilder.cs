using System.Text.Json;
using SyncFactors.Contracts;

namespace SyncFactors.Domain;

public static class RunEntrySnapshotBuilder
{
    private static readonly string[] SourceSnapshotKeys =
    [
        "emplStatus",
        "employmentStatus",
        "startDate",
        "endDate",
        "lastDateWorked",
        "latestTerminationDate",
        "lifecycleState",
        "managerId"
    ];

    public static JsonElement Build(
        string runId,
        bool dryRun,
        string syncScope,
        PlannedWorkerAction plan,
        string bucket,
        string? action,
        bool applied,
        bool succeeded,
        string? reason,
        DirectoryMutationCommand? plannedCommand,
        DirectoryCommandResult? liveResult,
        RunCaptureMetadata captureMetadata,
        string directoryIdentityAttribute = "sAMAccountName")
    {
        var managerId = GetAttribute(plan.Worker.Attributes, "managerId");
        var changedAttributeDetails = plan.AttributeChanges
            .Where(change => change.Changed)
            .Select(change => new
            {
                targetAttribute = change.Attribute,
                sourceField = change.Source,
                currentAdValue = ToNullableValue(change.Before),
                proposedValue = ToNullableValue(change.After)
            })
            .ToArray();
        var operations = plan.Operations
            .Select(operation => new
            {
                kind = operation.Kind,
                targetOu = operation.TargetOu
            })
            .ToArray();
        var sourceValues = BuildSourceValues(plan);
        var beforeAttributes = BuildDirectoryBeforeAttributes(plan);

        return ToJsonElement(new
        {
            workerId = plan.Worker.WorkerId,
            samAccountName = plan.Identity.SamAccountName,
            targetOu = plan.TargetOu,
            currentDistinguishedName = plan.DirectoryUser.DistinguishedName,
            emplStatus = GetAttribute(plan.Worker.Attributes, "emplStatus"),
            endDate = GetAttribute(plan.Worker.Attributes, "endDate"),
            currentOu = plan.CurrentOu,
            managerId,
            managerDistinguishedName = plan.ManagerDistinguishedName,
            reviewCategory = plan.ReviewCategory,
            reviewCaseType = plan.ReviewCaseType,
            reason,
            bucket,
            action,
            dryRun,
            applied,
            succeeded,
            matchedExistingUser = plan.Identity.MatchedExistingUser,
            proposedEnable = plan.TargetEnabled,
            currentEnabled = plan.CurrentEnabled,
            verifiedEnabled = liveResult?.VerifiedEnabled,
            verifiedDistinguishedName = liveResult?.VerifiedDistinguishedName,
            verifiedParentOu = liveResult?.VerifiedParentOu,
            proposedEmailAddress = plan.ProposedEmailAddress,
            missingSourceAttributes = plan.MissingSourceAttributes.Select(attribute => new
            {
                attribute = attribute.Attribute,
                reason = attribute.Reason
            }).ToArray(),
            operations,
            managerRequired = !string.IsNullOrWhiteSpace(managerId),
            changedAttributeDetails,
            decisionTree = (plan.DecisionSteps ?? [])
                .Select(step => new
                {
                    step = step.Step,
                    outcome = step.Outcome,
                    detail = step.Detail,
                    tone = step.Tone
                })
                .ToArray(),
            message = reason,
            sourceSnapshot = new
            {
                workerId = plan.Worker.WorkerId,
                preferredName = plan.Worker.PreferredName,
                lastName = plan.Worker.LastName,
                department = plan.Worker.Department,
                targetOu = plan.Worker.TargetOu,
                isPrehire = plan.Worker.IsPrehire,
                managerId,
                employmentStatus = GetAttribute(plan.Worker.Attributes, "emplStatus"),
                startDate = GetAttribute(plan.Worker.Attributes, "startDate"),
                endDate = GetAttribute(plan.Worker.Attributes, "endDate"),
                lastDateWorked = GetAttribute(plan.Worker.Attributes, "lastDateWorked"),
                latestTerminationDate = GetAttribute(plan.Worker.Attributes, "latestTerminationDate"),
                lifecycleState = GetAttribute(plan.Worker.Attributes, "lifecycleState"),
                mappedSourceValues = sourceValues
            },
            directoryBefore = new
            {
                matchedExistingUser = plan.Identity.MatchedExistingUser,
                samAccountName = plan.DirectoryUser.SamAccountName,
                distinguishedName = plan.DirectoryUser.DistinguishedName,
                currentOu = plan.CurrentOu,
                enabled = plan.CurrentEnabled,
                displayName = plan.DirectoryUser.DisplayName,
                identityAttribute = directoryIdentityAttribute,
                identityValue = ResolveDirectoryIdentityValue(plan.DirectoryUser, directoryIdentityAttribute),
                managerDistinguishedName = GetDirectoryAttribute(plan.DirectoryUser.Attributes, "manager"),
                trackedAttributes = beforeAttributes
            },
            plannedDirectoryState = new
            {
                samAccountName = plan.Identity.SamAccountName,
                commonName = plannedCommand?.CommonName ?? plan.Identity.SamAccountName,
                userPrincipalName = plannedCommand?.UserPrincipalName ?? plan.ProposedEmailAddress,
                mail = plannedCommand?.Mail ?? plan.ProposedEmailAddress,
                displayName = plannedCommand?.DisplayName,
                targetOu = plan.TargetOu,
                targetEnabled = plan.TargetEnabled,
                proposedManagerDistinguishedName = plan.ManagerDistinguishedName,
                changedAttributes = changedAttributeDetails,
                operations
            },
            plannedCommand = plannedCommand is null ? null : SanitizeCommand(plannedCommand),
            liveResult = dryRun || (!applied && liveResult is null)
                ? null
                : new
                {
                    applied,
                    succeeded = liveResult?.Succeeded ?? succeeded,
                    action = liveResult?.Action ?? action,
                    samAccountName = liveResult?.SamAccountName ?? plan.Identity.SamAccountName,
                    distinguishedName = liveResult?.DistinguishedName,
                    message = liveResult?.Message ?? reason,
                    runId = liveResult?.RunId,
                    verifiedEnabled = liveResult?.VerifiedEnabled,
                    verifiedDistinguishedName = liveResult?.VerifiedDistinguishedName,
                    verifiedParentOu = liveResult?.VerifiedParentOu
                },
            captureMetadata = new
            {
                schemaVersion = captureMetadata.SchemaVersion,
                runId = captureMetadata.RunId,
                dryRun = captureMetadata.DryRun,
                syncScope = captureMetadata.SyncScope,
                syncConfig = ToConfigSnapshot(captureMetadata.SyncConfig),
                mappingConfig = ToConfigSnapshot(captureMetadata.MappingConfig)
            }
        });
    }

    public static JsonElement BuildPlanningFailure(
        string runId,
        bool dryRun,
        string syncScope,
        WorkerSnapshot worker,
        string? reason,
        RunCaptureMetadata captureMetadata)
    {
        var managerId = GetAttribute(worker.Attributes, "managerId");
        return ToJsonElement(new
        {
            workerId = worker.WorkerId,
            samAccountName = (string?)null,
            targetOu = worker.TargetOu,
            emplStatus = GetAttribute(worker.Attributes, "emplStatus"),
            endDate = GetAttribute(worker.Attributes, "endDate"),
            managerDistinguishedName = (string?)null,
            reviewCategory = "ExternalSystem",
            reviewCaseType = "WorkerPlanningFailed",
            reason,
            bucket = "conflicts",
            action = (string?)null,
            dryRun,
            applied = false,
            succeeded = false,
            managerRequired = !string.IsNullOrWhiteSpace(managerId),
            changedAttributeDetails = Array.Empty<object>(),
            decisionTree = new[]
            {
                new
                {
                    step = "Source Worker",
                    outcome = "Loaded",
                    detail = $"Loaded source worker {worker.WorkerId} before planning failed.",
                    tone = "neutral"
                },
                new
                {
                    step = "Worker Planning",
                    outcome = "Failed",
                    detail = reason ?? string.Empty,
                    tone = "warn"
                }
            },
            sourceSnapshot = new
            {
                workerId = worker.WorkerId,
                preferredName = worker.PreferredName,
                lastName = worker.LastName,
                department = worker.Department,
                targetOu = worker.TargetOu,
                isPrehire = worker.IsPrehire,
                managerId,
                employmentStatus = GetAttribute(worker.Attributes, "emplStatus"),
                startDate = GetAttribute(worker.Attributes, "startDate"),
                endDate = GetAttribute(worker.Attributes, "endDate"),
                lastDateWorked = GetAttribute(worker.Attributes, "lastDateWorked"),
                latestTerminationDate = GetAttribute(worker.Attributes, "latestTerminationDate"),
                lifecycleState = GetAttribute(worker.Attributes, "lifecycleState"),
                mappedSourceValues = SourceSnapshotKeys
                    .Select(key => new { sourceField = key, value = GetAttribute(worker.Attributes, key) })
                    .Where(row => row.value is not null)
                    .ToArray()
            },
            directoryBefore = (object?)null,
            plannedDirectoryState = (object?)null,
            plannedCommand = (object?)null,
            liveResult = (object?)null,
            captureMetadata = new
            {
                schemaVersion = captureMetadata.SchemaVersion,
                runId = captureMetadata.RunId,
                dryRun = captureMetadata.DryRun,
                syncScope = captureMetadata.SyncScope,
                syncConfig = ToConfigSnapshot(captureMetadata.SyncConfig),
                mappingConfig = ToConfigSnapshot(captureMetadata.MappingConfig)
            }
        });
    }

    private static object SanitizeCommand(DirectoryMutationCommand command) => new
    {
        action = command.Action,
        workerId = command.WorkerId,
        managerId = command.ManagerId,
        managerDistinguishedName = command.ManagerDistinguishedName,
        samAccountName = command.SamAccountName,
        commonName = command.CommonName,
        userPrincipalName = command.UserPrincipalName,
        mail = command.Mail,
        targetOu = command.TargetOu,
        displayName = command.DisplayName,
        currentDistinguishedName = command.CurrentDistinguishedName,
        enableAccount = command.EnableAccount,
        operations = command.Operations.Select(operation => new
        {
            kind = operation.Kind,
            targetOu = operation.TargetOu
        }).ToArray(),
        attributes = command.Attributes
            .OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase)
            .Select(pair => new
            {
                attribute = pair.Key,
                value = pair.Value
            })
            .ToArray()
    };

    private static object? ToConfigSnapshot(RunConfigFingerprint? fingerprint) =>
        fingerprint is null
            ? null
            : new
            {
                path = fingerprint.Path,
                sha256 = fingerprint.Sha256
            };

    private static IReadOnlyList<object> BuildSourceValues(PlannedWorkerAction plan)
    {
        var keys = SourceSnapshotKeys
            .Concat(plan.AttributeChanges.Select(change => change.Source))
            .Concat(plan.MissingSourceAttributes.Select(attribute => attribute.Attribute))
            .Where(key => !string.IsNullOrWhiteSpace(key))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(key => key, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        return keys
            .Select(key => new
            {
                sourceField = key!,
                value = GetAttribute(plan.Worker.Attributes, key!)
            })
            .Where(row => row.value is not null)
            .Cast<object>()
            .ToArray();
    }

    private static IReadOnlyList<object> BuildDirectoryBeforeAttributes(PlannedWorkerAction plan)
    {
        var keys = plan.AttributeChanges
            .Select(change => change.Attribute)
            .Concat(["UserPrincipalName", "mail", "manager"])
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(key => key, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        return keys
            .Select(key => new
            {
                attribute = key,
                value = GetDirectoryAttribute(plan.DirectoryUser.Attributes, key)
            })
            .Where(row => row.value is not null)
            .Cast<object>()
            .ToArray();
    }

    private static string? GetAttribute(IReadOnlyDictionary<string, string?> attributes, string key)
    {
        if (attributes.TryGetValue(key, out var value))
        {
            return value;
        }

        var normalized = SourceAttributePathNormalizer.Normalize(key);
        return attributes.TryGetValue(normalized, out value) ? value : null;
    }

    private static string? GetDirectoryAttribute(IReadOnlyDictionary<string, string?> attributes, string key) =>
        attributes.TryGetValue(key, out var value) ? value : null;

    private static string? ResolveDirectoryIdentityValue(DirectoryUserSnapshot directoryUser, string directoryIdentityAttribute)
    {
        if (directoryUser.Attributes.TryGetValue(directoryIdentityAttribute, out var value) && !string.IsNullOrWhiteSpace(value))
        {
            return value;
        }

        return string.Equals(directoryIdentityAttribute, "sAMAccountName", StringComparison.OrdinalIgnoreCase)
            ? directoryUser.SamAccountName
            : null;
    }

    private static string? ToNullableValue(string value) =>
        string.Equals(value, "(unset)", StringComparison.Ordinal) ? null : value;

    private static JsonElement ToJsonElement<T>(T value) => JsonSerializer.SerializeToElement(value);
}

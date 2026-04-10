using SyncFactors.Contracts;

namespace SyncFactors.Domain;

public sealed class LifecyclePolicy(
    LifecyclePolicySettings settings) : ILifecyclePolicy
{
    public LifecycleDecision Evaluate(WorkerSnapshot worker, DirectoryUserSnapshot directoryUser)
    {
        var currentOu = DirectoryDistinguishedName.GetParentOu(directoryUser.DistinguishedName);
        var hasExistingUser = !string.IsNullOrWhiteSpace(directoryUser.SamAccountName);

        if (IsLeave(worker))
        {
            var leaveOu = string.IsNullOrWhiteSpace(settings.LeaveOu)
                ? settings.ActiveOu
                : settings.LeaveOu;

            if (!hasExistingUser)
            {
                return CreateDecision(
                    worker,
                    hasExistingUser,
                    bucket: "creates",
                    targetOu: leaveOu,
                    targetEnabled: false,
                    reason: "Leave worker should remain disabled in the leave OU.");
            }

            return CreateDecision(
                worker,
                hasExistingUser,
                bucket: string.Equals(currentOu, leaveOu, StringComparison.OrdinalIgnoreCase)
                    ? "disables"
                    : "updates",
                targetOu: leaveOu,
                targetEnabled: false,
                reason: "Leave worker should remain disabled in the leave OU.");
        }

        if (IsGraveyard(worker))
        {
            if (!hasExistingUser)
            {
                return CreateDecision(
                    worker,
                    hasExistingUser,
                    bucket: "unchanged",
                    targetOu: settings.GraveyardOu,
                    targetEnabled: false,
                    reason: "Inactive worker has no existing AD account.");
            }

            return CreateDecision(
                worker,
                hasExistingUser,
                bucket: string.Equals(currentOu, settings.GraveyardOu, StringComparison.OrdinalIgnoreCase)
                    ? "disables"
                    : "graveyardMoves",
                targetOu: settings.GraveyardOu,
                targetEnabled: false,
                reason: "Inactive worker should be disabled and placed in the graveyard OU.");
        }

        if (worker.IsPrehire)
        {
            return CreateDecision(
                worker,
                hasExistingUser,
                bucket: hasExistingUser ? "updates" : "creates",
                targetOu: settings.PrehireOu,
                targetEnabled: false,
                reason: "Prehire accounts remain disabled in the prehire OU until the start date.");
        }

        var needsActivationMove = hasExistingUser &&
            string.Equals(currentOu, settings.PrehireOu, StringComparison.OrdinalIgnoreCase);
        var needsEnable = hasExistingUser && directoryUser.Enabled == false;
        var bucket = hasExistingUser
            ? needsActivationMove || needsEnable ? "enables" : "updates"
            : "creates";

        return CreateDecision(
            worker,
            hasExistingUser,
            bucket: bucket,
            targetOu: settings.ActiveOu,
            targetEnabled: true,
            reason: hasExistingUser
                ? "Active worker should be present and enabled in the active OU."
                : "Active worker requires a new AD account in the active OU.");
    }

    private LifecycleDecision CreateDecision(
        WorkerSnapshot worker,
        bool hasExistingUser,
        string bucket,
        string targetOu,
        bool targetEnabled,
        string? reason)
    {
        if (!hasExistingUser &&
            string.Equals(bucket, "creates", StringComparison.OrdinalIgnoreCase) &&
            !targetEnabled &&
            ShouldSkipDisabledCreate(worker))
        {
            return new LifecycleDecision(
                Bucket: "unchanged",
                TargetOu: targetOu,
                TargetEnabled: false,
                Reason: "Skipping disabled account creation because the worker is already past deletion retention.");
        }

        return new LifecycleDecision(
            Bucket: bucket,
            TargetOu: targetOu,
            TargetEnabled: targetEnabled,
            Reason: reason);
    }

    private bool ShouldSkipDisabledCreate(WorkerSnapshot worker)
    {
        if (!settings.SkipCreateIfPastDeletionRetention)
        {
            return false;
        }

        if (settings.DeletionRetentionDays < 0)
        {
            return false;
        }

        if (!TryResolveAttribute(worker.Attributes, settings.InactiveDateField, out var inactiveDateValue) ||
            !SourceDateParser.TryParse(inactiveDateValue, out var inactiveDate))
        {
            return false;
        }

        var deletionEligibleDate = inactiveDate.Date.AddDays(settings.DeletionRetentionDays);
        return deletionEligibleDate < DateTimeOffset.UtcNow.Date;
    }

    private bool IsGraveyard(WorkerSnapshot worker)
    {
        if (!TryResolveAttribute(worker.Attributes, settings.InactiveStatusField, out var status) ||
            string.IsNullOrWhiteSpace(status))
        {
            return false;
        }

        return settings.InactiveStatusValues.Any(value =>
            string.Equals(value, status, StringComparison.OrdinalIgnoreCase));
    }

    private bool IsLeave(WorkerSnapshot worker)
    {
        if (!TryResolveAttribute(worker.Attributes, settings.InactiveStatusField, out var status) ||
            string.IsNullOrWhiteSpace(status))
        {
            return false;
        }

        return (settings.LeaveStatusValues ?? []).Any(value =>
            string.Equals(value, status, StringComparison.OrdinalIgnoreCase));
    }

    private static bool TryResolveAttribute(
        IReadOnlyDictionary<string, string?> attributes,
        string key,
        out string? value)
    {
        if (attributes.TryGetValue(key, out value))
        {
            return true;
        }

        var normalized = SourceAttributePathNormalizer.Normalize(key);
        if (!string.Equals(normalized, key, StringComparison.OrdinalIgnoreCase) &&
            attributes.TryGetValue(normalized, out value))
        {
            return true;
        }

        value = null;
        return false;
    }
}

public static class DirectoryDistinguishedName
{
    public static string GetParentOu(string? distinguishedName)
    {
        if (string.IsNullOrWhiteSpace(distinguishedName))
        {
            return string.Empty;
        }

        var separatorIndex = FindFirstUnescapedComma(distinguishedName);
        return separatorIndex < 0 || separatorIndex == distinguishedName.Length - 1
            ? string.Empty
            : distinguishedName[(separatorIndex + 1)..];
    }

    private static int FindFirstUnescapedComma(string distinguishedName)
    {
        for (var index = 0; index < distinguishedName.Length; index++)
        {
            if (distinguishedName[index] != ',')
            {
                continue;
            }

            var backslashCount = 0;
            for (var lookbehind = index - 1; lookbehind >= 0 && distinguishedName[lookbehind] == '\\'; lookbehind--)
            {
                backslashCount++;
            }

            if (backslashCount % 2 == 0)
            {
                return index;
            }
        }

        return -1;
    }
}

using SyncFactors.Contracts;

namespace SyncFactors.Domain;

public sealed class LifecyclePolicy(
    LifecyclePolicySettings settings) : ILifecyclePolicy
{
    public LifecycleDecision Evaluate(WorkerSnapshot worker, DirectoryUserSnapshot directoryUser)
    {
        var currentOu = DirectoryDistinguishedName.GetParentOu(directoryUser.DistinguishedName);
        var hasExistingUser = !string.IsNullOrWhiteSpace(directoryUser.SamAccountName);

        if (IsInactive(worker))
        {
            if (!hasExistingUser)
            {
                return new LifecycleDecision(
                    Bucket: "unchanged",
                    TargetOu: settings.GraveyardOu,
                    TargetEnabled: false,
                    Reason: "Inactive worker has no existing AD account.");
            }

            return new LifecycleDecision(
                Bucket: string.Equals(currentOu, settings.GraveyardOu, StringComparison.OrdinalIgnoreCase)
                    ? "disables"
                    : "graveyardMoves",
                TargetOu: settings.GraveyardOu,
                TargetEnabled: false,
                Reason: "Inactive worker should be disabled and placed in the graveyard OU.");
        }

        if (worker.IsPrehire)
        {
            return new LifecycleDecision(
                Bucket: hasExistingUser ? "updates" : "creates",
                TargetOu: settings.PrehireOu,
                TargetEnabled: false,
                Reason: "Prehire accounts remain disabled in the prehire OU until the start date.");
        }

        var needsActivationMove = hasExistingUser &&
            string.Equals(currentOu, settings.PrehireOu, StringComparison.OrdinalIgnoreCase);
        var needsEnable = hasExistingUser && directoryUser.Enabled == false;
        var bucket = hasExistingUser
            ? needsActivationMove || needsEnable ? "enables" : "updates"
            : "creates";

        return new LifecycleDecision(
            Bucket: bucket,
            TargetOu: settings.ActiveOu,
            TargetEnabled: true,
            Reason: hasExistingUser
                ? "Active worker should be present and enabled in the active OU."
                : "Active worker requires a new AD account in the active OU.");
    }

    private bool IsInactive(WorkerSnapshot worker)
    {
        if (!TryResolveAttribute(worker.Attributes, settings.InactiveStatusField, out var status) ||
            string.IsNullOrWhiteSpace(status))
        {
            return false;
        }

        return settings.InactiveStatusValues.Any(value =>
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

internal static class DirectoryDistinguishedName
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

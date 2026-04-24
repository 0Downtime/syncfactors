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
                return new LifecycleDecision(
                    Bucket: "creates",
                    TargetOu: leaveOu,
                    TargetEnabled: false,
                    Reason: "Leave worker should remain disabled in the leave OU.");
            }

            return new LifecycleDecision(
                Bucket: string.Equals(currentOu, leaveOu, StringComparison.OrdinalIgnoreCase)
                    ? "disables"
                    : "updates",
                TargetOu: leaveOu,
                TargetEnabled: false,
                Reason: "Leave worker should remain disabled in the leave OU.");
        }

        if (IsGraveyard(worker))
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
                TargetEnabled: true,
                Reason: "Prehire accounts remain enabled in the prehire OU until the start date.");
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
        foreach (var candidate in BuildLookupCandidates(key))
        {
            if (attributes.TryGetValue(candidate, out value))
            {
                return true;
            }
        }

        value = null;
        return false;
    }

    private static IEnumerable<string> BuildLookupCandidates(string key)
    {
        var candidates = new List<string>();
        AddCandidate(candidates, key);

        var normalized = SourceAttributePathNormalizer.Normalize(key);
        AddCandidate(candidates, normalized);

        var indexedPath = ToIndexedNavigationPath(key);
        AddCandidate(candidates, indexedPath);
        if (!string.IsNullOrWhiteSpace(indexedPath))
        {
            AddCandidate(candidates, SourceAttributePathNormalizer.Normalize(indexedPath));
        }

        var leafName = GetLeafName(key);
        AddCandidate(candidates, leafName);
        if (!string.IsNullOrWhiteSpace(leafName))
        {
            AddCandidate(candidates, SourceAttributePathNormalizer.Normalize(leafName));
        }

        var employmentStatusAliases = BuildEmploymentStatusAliases(key, normalized, leafName);
        if (employmentStatusAliases.Count > 0)
        {
            foreach (var alias in employmentStatusAliases)
            {
                AddCandidate(candidates, alias);
            }
        }

        return candidates;
    }

    private static IReadOnlyList<string> BuildEmploymentStatusAliases(string key, string normalized, string? leafName)
    {
        if (!IsEmploymentStatusKey(key) &&
            !IsEmploymentStatusKey(normalized) &&
            !IsEmploymentStatusKey(leafName))
        {
            return [];
        }

        return
        [
            "emplStatus",
            "employeeStatus",
            "employeestatus",
            "employmentNav[0].jobInfoNav[0].emplStatus",
            "employmentNav/jobInfoNav/emplStatus",
            "employmentNav[0].jobInfoNav[0].employeeStatus",
            "employmentNav/jobInfoNav/employeeStatus",
            "employmentNav[0].jobInfoNav[0].employeestatus",
            "employmentNav/jobInfoNav/employeestatus"
        ];
    }

    private static bool IsEmploymentStatusKey(string? key)
    {
        return !string.IsNullOrWhiteSpace(key) &&
               (string.Equals(key, "emplStatus", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(key, "employeeStatus", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(key, "employeestatus", StringComparison.OrdinalIgnoreCase));
    }

    private static string? ToIndexedNavigationPath(string key)
    {
        if (string.IsNullOrWhiteSpace(key) || !key.Contains('/', StringComparison.Ordinal))
        {
            return null;
        }

        var segments = key
            .Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToArray();

        if (segments.Length == 0)
        {
            return null;
        }

        for (var index = 0; index < segments.Length - 1; index++)
        {
            if (!segments[index].Contains('[', StringComparison.Ordinal) &&
                segments[index].EndsWith("Nav", StringComparison.OrdinalIgnoreCase))
            {
                segments[index] = $"{segments[index]}[0]";
            }
        }

        return string.Join(".", segments);
    }

    private static string? GetLeafName(string key)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            return null;
        }

        var segments = key
            .Split(['/', '.'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return segments.Length == 0 ? null : segments[^1];
    }

    private static void AddCandidate(ICollection<string> candidates, string? candidate)
    {
        if (string.IsNullOrWhiteSpace(candidate) ||
            candidates.Contains(candidate, StringComparer.OrdinalIgnoreCase))
        {
            return;
        }

        candidates.Add(candidate);
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

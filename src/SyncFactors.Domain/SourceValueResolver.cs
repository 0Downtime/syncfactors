using SyncFactors.Contracts;

namespace SyncFactors.Domain;

internal static class SourceValueResolver
{
    public static string? ResolveSourceValue(
        WorkerSnapshot worker,
        string source,
        string target,
        string? proposedEmailAddress)
    {
        if (target is "UserPrincipalName" or "mail")
        {
            return proposedEmailAddress ?? DirectoryIdentityFormatter.BuildEmailAddress(
                DirectoryIdentityFormatter.BuildBaseEmailLocalPart(worker.PreferredName, worker.LastName));
        }

        if (AttributeDiffService.TryParseConcatSource(source, out var keys))
        {
            var parts = keys
                .Select(key => ResolveSingleSourceValue(worker, key))
                .Where(part => !string.IsNullOrWhiteSpace(part))
                .Select(part => part!.Trim())
                .ToArray();

            return parts.Length == 0 ? null : string.Join(' ', parts);
        }

        return ResolveSingleSourceValue(worker, source);
    }

    private static string? ResolveSingleSourceValue(WorkerSnapshot worker, string source)
    {
        if (worker.Attributes.TryGetValue(source, out var directValue))
        {
            return directValue;
        }

        var normalizedSource = SourceAttributePathNormalizer.Normalize(source);
        if (!string.Equals(normalizedSource, source, StringComparison.OrdinalIgnoreCase)
            && worker.Attributes.TryGetValue(normalizedSource, out var normalizedValue))
        {
            return normalizedValue;
        }

        return normalizedSource switch
        {
            "preferredName" => worker.PreferredName,
            "firstName" => worker.PreferredName,
            "lastName" => worker.LastName,
            "department" => worker.Department,
            "startDate" => worker.Attributes.TryGetValue("startDate", out var startDate) ? startDate : null,
            _ => null
        };
    }
}

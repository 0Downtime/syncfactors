using SyncFactors.Contracts;

namespace SyncFactors.Domain;

public sealed class IdentityMatcher : IIdentityMatcher
{
    public IdentityMatchResult Match(WorkerSnapshot worker, DirectoryUserSnapshot? directoryUser)
    {
        if (!string.IsNullOrWhiteSpace(directoryUser?.SamAccountName))
        {
            return new IdentityMatchResult(
                Bucket: "updates",
                MatchedExistingUser: true,
                SamAccountName: directoryUser.SamAccountName!,
                Reason: "Native preview matched an existing directory account.",
                OperatorActionSummary: "Update account preview");
        }

        return new IdentityMatchResult(
            Bucket: "creates",
            MatchedExistingUser: false,
            SamAccountName: $"{worker.PreferredName}.{worker.LastName}".ToLowerInvariant(),
            Reason: "Native preview planned a new directory account.",
            OperatorActionSummary: "Create account preview");
    }
}

public sealed class AttributeDiffService : IAttributeDiffService
{
    private readonly IAttributeMappingProvider _mappingProvider;

    public AttributeDiffService(IAttributeMappingProvider mappingProvider)
    {
        _mappingProvider = mappingProvider;
    }

    public IReadOnlyList<AttributeChange> BuildDiff(WorkerSnapshot worker, DirectoryUserSnapshot? directoryUser)
    {
        var currentAttributes = directoryUser?.Attributes
            ?? new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);

        var changes = _mappingProvider.GetEnabledMappings()
            .Select(mapping =>
            {
                var proposedValue = Transform(GetSourceValue(worker, mapping.Source), mapping.Transform);
                var currentValue = GetDirectoryValue(currentAttributes, mapping.Target);
                var before = string.IsNullOrWhiteSpace(currentValue) ? "(unset)" : currentValue!;
                var after = string.IsNullOrWhiteSpace(proposedValue) ? "(unset)" : proposedValue!;

                return new AttributeChange(
                    Attribute: mapping.Target,
                    Source: mapping.Source,
                    Before: before,
                    After: after,
                    Changed: !string.Equals(before, after, StringComparison.Ordinal));
            })
            .Where(change => change.Changed)
            .ToList();

        var proposedDisplayName = $"{worker.PreferredName} {worker.LastName}";
        var currentDisplayName = GetDirectoryValue(currentAttributes, "displayName");
        if (!string.Equals(currentDisplayName, proposedDisplayName, StringComparison.Ordinal))
        {
            changes.Insert(0, new AttributeChange(
                Attribute: "displayName",
                Source: "firstName,lastName",
                Before: string.IsNullOrWhiteSpace(currentDisplayName) ? "(unset)" : currentDisplayName!,
                After: proposedDisplayName,
                Changed: true));
        }

        return changes;
    }

    private static string? GetSourceValue(WorkerSnapshot worker, string source)
    {
        if (worker.Attributes.TryGetValue(source, out var directValue))
        {
            return directValue;
        }

        var normalizedSource = NormalizeSourcePath(source);
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

    private static string? GetDirectoryValue(IReadOnlyDictionary<string, string?> attributes, string target)
    {
        return attributes.TryGetValue(target, out var value) ? value : null;
    }

    private static string? Transform(string? value, string transform)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return value;
        }

        return transform switch
        {
            "Trim" => value.Trim(),
            "Lower" => value.Trim().ToLowerInvariant(),
            "DateOnly" => DateTimeOffset.TryParse(value, out var parsed)
                ? parsed.ToString("yyyy-MM-dd")
                : value,
            _ => value
        };
    }

    private static string NormalizeSourcePath(string source)
    {
        return source switch
        {
            "personalInfoNav[0].firstName" => "firstName",
            "personalInfoNav[0].lastName" => "lastName",
            "emailNav[0].emailAddress" => "email",
            "employmentNav[0].startDate" => "startDate",
            "employmentNav[0].jobInfoNav[0].department" => "department",
            "employmentNav[0].jobInfoNav[0].company" => "company",
            "employmentNav[0].jobInfoNav[0].location" => "location",
            "employmentNav[0].jobInfoNav[0].jobTitle" => "jobTitle",
            "employmentNav[0].jobInfoNav[0].businessUnit" => "businessUnit",
            "employmentNav[0].jobInfoNav[0].division" => "division",
            "employmentNav[0].jobInfoNav[0].costCenter" => "costCenter",
            "employmentNav[0].jobInfoNav[0].employeeClass" => "employeeClass",
            "employmentNav[0].jobInfoNav[0].employeeType" => "employeeType",
            "employmentNav[0].jobInfoNav[0].managerId" => "managerId",
            _ => source
        };
    }
}

using SyncFactors.Contracts;
using Microsoft.Extensions.Logging;

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
            SamAccountName: worker.WorkerId,
            Reason: "Native preview planned a new directory account.",
            OperatorActionSummary: "Create account preview");
    }
}

public sealed class AttributeDiffService : IAttributeDiffService
{
    private readonly IAttributeMappingProvider _mappingProvider;
    private readonly ILogger<AttributeDiffService> _logger;

    public AttributeDiffService(
        IAttributeMappingProvider mappingProvider,
        ILogger<AttributeDiffService> logger)
    {
        _mappingProvider = mappingProvider;
        _logger = logger;
    }

    public IReadOnlyList<AttributeChange> BuildDiff(WorkerSnapshot worker, DirectoryUserSnapshot? directoryUser)
    {
        var currentAttributes = directoryUser?.Attributes
            ?? new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);

        var enabledMappings = _mappingProvider.GetEnabledMappings();
        var changes = enabledMappings
            .Select(mapping =>
            {
                var sourceValue = GetSourceValue(worker, mapping.Source, mapping.Target);
                var proposedValue = Transform(sourceValue, mapping.Transform);
                var currentValue = GetDirectoryValue(currentAttributes, mapping.Target);
                var before = string.IsNullOrWhiteSpace(currentValue) ? "(unset)" : currentValue!;
                var after = string.IsNullOrWhiteSpace(proposedValue) ? "(unset)" : proposedValue!;
                var changed = !string.Equals(before, after, StringComparison.Ordinal);

                _logger.LogDebug(
                    "Evaluated attribute mapping. WorkerId={WorkerId} Target={Target} Source={Source} SourceValue={SourceValue} CurrentValue={CurrentValue} ProposedValue={ProposedValue} Changed={Changed}",
                    worker.WorkerId,
                    mapping.Target,
                    mapping.Source,
                    string.IsNullOrWhiteSpace(sourceValue) ? "(unset)" : sourceValue,
                    string.IsNullOrWhiteSpace(currentValue) ? "(unset)" : currentValue,
                    string.IsNullOrWhiteSpace(proposedValue) ? "(unset)" : proposedValue,
                    changed);

                return new AttributeChange(
                    Attribute: mapping.Target,
                    Source: mapping.Source,
                    Before: before,
                    After: after,
                    Changed: changed);
            })
            .Where(change => change.Changed)
            .ToList();

        var proposedDisplayName = DirectoryIdentityFormatter.BuildDisplayName(worker.PreferredName, worker.LastName);
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

        _logger.LogDebug(
            "Attribute diff completed. WorkerId={WorkerId} EnabledMappings={EnabledMappings} ChangedMappings={ChangedMappings}",
            worker.WorkerId,
            enabledMappings.Count,
            changes.Count);

        return changes;
    }

    private static string? GetSourceValue(WorkerSnapshot worker, string source, string target)
    {
        if (target is "UserPrincipalName" or "mail")
        {
            return DirectoryIdentityFormatter.BuildEmailAddress(
                DirectoryIdentityFormatter.BuildBaseEmailLocalPart(worker.PreferredName, worker.LastName));
        }

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
            "employmentNav[0].jobInfoNav[0].departmentNav.department" => "department",
            "employmentNav[0].jobInfoNav[0].company" => "company",
            "employmentNav[0].jobInfoNav[0].companyNav.company" => "company",
            "employmentNav[0].jobInfoNav[0].location" => "location",
            "employmentNav[0].jobInfoNav[0].locationNav.LocationName" => "location",
            "employmentNav[0].jobInfoNav[0].jobTitle" => "jobTitle",
            "employmentNav[0].jobInfoNav[0].businessUnit" => "businessUnit",
            "employmentNav[0].jobInfoNav[0].businessUnitNav.businessUnit" => "businessUnit",
            "employmentNav[0].jobInfoNav[0].division" => "division",
            "employmentNav[0].jobInfoNav[0].divisionNav.division" => "division",
            "employmentNav[0].jobInfoNav[0].costCenter" => "costCenter",
            "employmentNav[0].jobInfoNav[0].costCenterNav.costCenterDescription" => "costCenter",
            "employmentNav[0].jobInfoNav[0].employeeClass" => "employeeClass",
            "employmentNav[0].jobInfoNav[0].employeeType" => "employeeType",
            "employmentNav[0].jobInfoNav[0].managerId" => "managerId",
            "employmentNav[0].jobInfoNav[0].customString3" => "peopleGroup",
            "employmentNav[0].jobInfoNav[0].customString20" => "leadershipLevel",
            "employmentNav[0].jobInfoNav[0].customString87" => "region",
            "employmentNav[0].jobInfoNav[0].customString110" => "geozone",
            "employmentNav[0].jobInfoNav[0].customString111" => "bargainingUnit",
            "employmentNav[0].jobInfoNav[0].customString91" => "unionJobCode",
            "employmentNav[0].jobInfoNav[0].customString112" => "cintasUniformCategory",
            "employmentNav[0].jobInfoNav[0].customString113" => "cintasUniformAllotment",
            _ => source
        };
    }
}

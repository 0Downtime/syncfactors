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
    private readonly IWorkerPreviewLogWriter _logWriter;

    public AttributeDiffService(
        IAttributeMappingProvider mappingProvider,
        IWorkerPreviewLogWriter logWriter,
        ILogger<AttributeDiffService> logger)
    {
        _mappingProvider = mappingProvider;
        _logWriter = logWriter;
        _logger = logger;
    }

    public async Task<IReadOnlyList<AttributeChange>> BuildDiffAsync(
        WorkerSnapshot worker,
        DirectoryUserSnapshot? directoryUser,
        string? logPath,
        CancellationToken cancellationToken)
    {
        var currentAttributes = directoryUser?.Attributes
            ?? new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);

        var enabledMappings = _mappingProvider.GetEnabledMappings();
        if (!string.IsNullOrWhiteSpace(logPath))
        {
            await _logWriter.AppendAsync(logPath, new WorkerPreviewLogEntry(
                Event: "preview.diff.start",
                WorkerId: worker.WorkerId,
                Timestamp: DateTimeOffset.UtcNow,
                Target: null,
                Source: null,
                SourceValue: null,
                CurrentValue: null,
                ProposedValue: null,
                Changed: null,
                Message: $"Evaluating {enabledMappings.Count} enabled mappings."), cancellationToken);
        }

        var changes = new List<AttributeChange>();
        foreach (var mapping in enabledMappings)
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

            if (!string.IsNullOrWhiteSpace(logPath))
            {
                await _logWriter.AppendAsync(logPath, new WorkerPreviewLogEntry(
                    Event: "preview.diff.mapping",
                    WorkerId: worker.WorkerId,
                    Timestamp: DateTimeOffset.UtcNow,
                    Target: mapping.Target,
                    Source: mapping.Source,
                    SourceValue: sourceValue,
                    CurrentValue: currentValue,
                    ProposedValue: proposedValue,
                    Changed: changed,
                    Message: changed ? "Changed" : "Unchanged or filtered"), cancellationToken);
            }

            if (changed)
            {
                changes.Add(new AttributeChange(
                    Attribute: mapping.Target,
                    Source: mapping.Source,
                    Before: before,
                    After: after,
                    Changed: true));
            }
        }

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

        if (!string.IsNullOrWhiteSpace(logPath))
        {
            await _logWriter.AppendAsync(logPath, new WorkerPreviewLogEntry(
                Event: "preview.diff.complete",
                WorkerId: worker.WorkerId,
                Timestamp: DateTimeOffset.UtcNow,
                Target: null,
                Source: null,
                SourceValue: null,
                CurrentValue: null,
                ProposedValue: null,
                Changed: null,
                Message: $"Completed with {changes.Count} changed mappings."), cancellationToken);
        }

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
            "personalInfoNav[0].preferredName" => "preferredName",
            "personalInfoNav[0].displayName" => "displayName",
            "emailNav[0].emailAddress" => "email",
            "emailNav[?(@.isPrimary == true)].emailAddress" => "email",
            "emailNav[?(@.isPrimary == true)].emailType" => "emailType",
            "emailNav[?(@.isPrimary == true)].isPrimary" => "emailIsPrimary",
            "employmentNav[0].startDate" => "startDate",
            "employmentNav[0].jobInfoNav[0].department" => "department",
            "employmentNav[0].jobInfoNav[0].departmentNav.department" => "department",
            "employmentNav[0].jobInfoNav[0].departmentNav.name_localized" => "department",
            "employmentNav[0].jobInfoNav[0].departmentNav.name" => "department",
            "employmentNav[0].jobInfoNav[0].company" => "company",
            "employmentNav[0].jobInfoNav[0].companyNav.company" => "company",
            "employmentNav[0].jobInfoNav[0].companyNav.name_localized" => "company",
            "employmentNav[0].jobInfoNav[0].location" => "location",
            "employmentNav[0].jobInfoNav[0].locationNav.LocationName" => "location",
            "employmentNav[0].jobInfoNav[0].locationNav.name" => "location",
            "employmentNav[0].jobInfoNav[0].jobTitle" => "jobTitle",
            "employmentNav[0].jobInfoNav[0].businessUnit" => "businessUnit",
            "employmentNav[0].jobInfoNav[0].businessUnitNav.businessUnit" => "businessUnit",
            "employmentNav[0].jobInfoNav[0].businessUnitNav.name_localized" => "businessUnit",
            "employmentNav[0].jobInfoNav[0].division" => "division",
            "employmentNav[0].jobInfoNav[0].divisionNav.division" => "division",
            "employmentNav[0].jobInfoNav[0].divisionNav.name_localized" => "division",
            "employmentNav[0].jobInfoNav[0].costCenter" => "costCenter",
            "employmentNav[0].jobInfoNav[0].costCenterNav.costCenterDescription" => "costCenter",
            "employmentNav[0].jobInfoNav[0].costCenterNav.name_localized" => "costCenter",
            "employmentNav[0].jobInfoNav[0].costCenterNav.description_localized" => "costCenterDescription",
            "employmentNav[0].jobInfoNav[0].costCenterNav.externalCode" => "costCenterId",
            "employmentNav[0].jobInfoNav[0].employeeClass" => "employeeClass",
            "employmentNav[0].jobInfoNav[0].employeeType" => "employeeType",
            "employmentNav[0].jobInfoNav[0].managerId" => "managerId",
            "employmentNav[0].userNav.manager.empInfo.personIdExternal" => "managerId",
            "employmentNav[0].jobInfoNav[0].customString3" => "peopleGroup",
            "employmentNav[0].jobInfoNav[0].customString20" => "leadershipLevel",
            "employmentNav[0].jobInfoNav[0].customString87" => "region",
            "employmentNav[0].jobInfoNav[0].customString110" => "geozone",
            "employmentNav[0].jobInfoNav[0].customString111" => "bargainingUnit",
            "employmentNav[0].jobInfoNav[0].customString91" => "unionJobCode",
            "employmentNav[0].jobInfoNav[0].customString112" => "cintasUniformCategory",
            "employmentNav[0].jobInfoNav[0].customString113" => "cintasUniformAllotment",
            "employmentNav[0].jobInfoNav[0].locationNav.addressNavDEFLT.address1" => "officeLocationAddress",
            "employmentNav[0].jobInfoNav[0].locationNav.addressNavDEFLT.city" => "officeLocationCity",
            "employmentNav[0].jobInfoNav[0].locationNav.addressNavDEFLT.zipCode" => "officeLocationZipCode",
            "employmentNav[0].jobInfoNav[0].locationNav.addressNavDEFLT.customString4" => "officeLocationCustomString4",
            "employmentNav[0].jobInfoNav[0].companyNav.externalCode" => "companyId",
            "employmentNav[0].jobInfoNav[0].businessUnitNav.externalCode" => "businessUnitId",
            "employmentNav[0].jobInfoNav[0].divisionNav.externalCode" => "divisionId",
            "employmentNav[0].jobInfoNav[0].departmentNav.externalCode" => "departmentId",
            "employmentNav[0].jobInfoNav[0].jobCodeNav.name_localized" => "jobCode",
            "employmentNav[0].jobInfoNav[0].jobCodeNav.externalCode" => "jobCodeId",
            "employmentNav[0].jobInfoNav[0].payGradeNav.name" => "payGrade",
            "employmentNav[0].jobInfoNav[0].position" => "position",
            _ => source
        };
    }
}

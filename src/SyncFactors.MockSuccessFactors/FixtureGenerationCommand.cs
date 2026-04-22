using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace SyncFactors.MockSuccessFactors;

public sealed record FixtureGenerationRequest(
    string InputPath,
    string OutputPath,
    string? ManifestPath);

public static class FixtureGenerationCommand
{
    public static FixtureGenerationRequest? TryParse(string[] args)
    {
        if (args.Length == 0 || !string.Equals(args[0], "generate-fixtures", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        string? input = null;
        string? output = null;
        string? manifest = null;

        for (var index = 1; index < args.Length; index += 2)
        {
            if (index + 1 >= args.Length)
            {
                throw new InvalidOperationException("Expected a value after command argument.");
            }

            var key = args[index];
            var value = args[index + 1];
            switch (key)
            {
                case "--input":
                    input = value;
                    break;
                case "--output":
                    output = value;
                    break;
                case "--manifest":
                    manifest = value;
                    break;
                default:
                    throw new InvalidOperationException($"Unsupported argument '{key}'.");
            }
        }

        if (string.IsNullOrWhiteSpace(input) || string.IsNullOrWhiteSpace(output))
        {
            throw new InvalidOperationException("Usage: generate-fixtures --input <path> --output <path> [--manifest <path>]");
        }

        return new FixtureGenerationRequest(Path.GetFullPath(input), Path.GetFullPath(output), manifest is null ? null : Path.GetFullPath(manifest));
    }

    public static async Task<int> RunAsync(FixtureGenerationRequest command, TextWriter output, CancellationToken cancellationToken)
    {
        var json = await File.ReadAllTextAsync(command.InputPath, cancellationToken);
        using var document = JsonDocument.Parse(json);

        var workers = ExtractWorkers(document.RootElement)
            .Select(ParseFixture)
            .Select((fixture, index) => SanitizeFixture(fixture, index))
            .ToArray();

        var fixtureDocument = new MockFixtureDocument(workers);
        Directory.CreateDirectory(Path.GetDirectoryName(command.OutputPath)!);
        await File.WriteAllTextAsync(
            command.OutputPath,
            JsonSerializer.Serialize(fixtureDocument, SerializerContext.Default.MockFixtureDocument),
            cancellationToken);

        if (!string.IsNullOrWhiteSpace(command.ManifestPath))
        {
            Directory.CreateDirectory(Path.GetDirectoryName(command.ManifestPath)!);
            var manifest = new FixtureManifest(
                SourcePath: command.InputPath,
                GeneratedAtUtc: DateTimeOffset.UtcNow.ToString("O"),
                SanitizationProfile: "deterministic-pseudonym-v1",
                WorkerCount: workers.Length,
                ScenarioCounts: workers
                    .SelectMany(worker => worker.ScenarioTags)
                    .GroupBy(tag => tag, StringComparer.OrdinalIgnoreCase)
                    .ToDictionary(group => group.Key, group => group.Count(), StringComparer.OrdinalIgnoreCase));
            await File.WriteAllTextAsync(
                command.ManifestPath,
                JsonSerializer.Serialize(manifest, SerializerContext.Default.FixtureManifest),
                cancellationToken);
        }

        MockFixtureSummaryReporter.WriteSummary(output, fixtureDocument, "generated fixtures");
        await output.WriteLineAsync($"Generated {workers.Length} sanitized fixtures at {command.OutputPath}");
        return 0;
    }

    private static IReadOnlyList<JsonElement> ExtractWorkers(JsonElement root)
    {
        if (root.TryGetProperty("d", out var d) &&
            d.TryGetProperty("results", out var dResults) &&
            dResults.ValueKind == JsonValueKind.Array)
        {
            return dResults.EnumerateArray().Select(item => item.Clone()).ToArray();
        }

        if (root.TryGetProperty("value", out var value) && value.ValueKind == JsonValueKind.Array)
        {
            return value.EnumerateArray().Select(item => item.Clone()).ToArray();
        }

        return [];
    }

    private static MockWorkerFixture ParseFixture(JsonElement worker)
    {
        var personalInfo = GetFirstResult(worker, "personalInfoNav");
        var employment = GetFirstResult(worker, "employmentNav");
        var jobInfo = employment is { ValueKind: not JsonValueKind.Undefined }
            ? GetFirstResult(employment.Value, "jobInfoNav")
            : null;
        var email = GetPreferredResult(worker, "emailNav", static item => IsTrue(item, "isPrimary"))
            ?? GetFirstResult(worker, "emailNav");
        var primaryPhone = GetPreferredResult(worker, "phoneNav", static item => IsTrue(item, "isPrimary"))
            ?? GetFirstResult(worker, "phoneNav");
        var businessPhone = GetPreferredResult(worker, "phoneNav", static item => PropertyEquals(item, "phoneType", "10605"));
        var cellPhone = GetPreferredResult(worker, "phoneNav", static item => PropertyEquals(item, "phoneType", "10606"));
        var location = jobInfo is { ValueKind: not JsonValueKind.Undefined } ? GetObject(jobInfo.Value, "locationNav") : null;
        var address = location is { ValueKind: not JsonValueKind.Undefined } ? GetObject(location.Value, "addressNavDEFLT") : null;
        var companyNav = GetObject(jobInfo, "companyNav");
        var departmentNav = GetObject(jobInfo, "departmentNav");
        var businessUnitNav = GetObject(jobInfo, "businessUnitNav");
        var divisionNav = GetObject(jobInfo, "divisionNav");
        var costCenterNav = GetObject(jobInfo, "costCenterNav");
        var companyCountryNav = GetObject(companyNav, "countryOfRegistrationNav");
        var userNav = GetObject(employment, "userNav");
        var managerNav = GetObject(userNav, "manager");
        var managerEmpInfo = GetObject(managerNav, "empInfo");
        var payGradeNav = GetObject(jobInfo, "payGradeNav");
        var personEmpTerminationInfoNav = GetObject(worker, "personEmpTerminationInfoNav");

        var startDate = GetString(employment, "startDate") ?? GetString(worker, "startDate") ?? DateTimeOffset.UtcNow.ToString("O");
        var department = GetString(departmentNav, "name_localized") ?? GetString(departmentNav, "department") ?? GetString(jobInfo, "department");
        var company = GetString(companyNav, "name_localized") ?? GetString(companyNav, "company") ?? GetString(jobInfo, "company");
        var locationName = GetString(location, "name") ?? GetString(location, "LocationName") ?? GetString(jobInfo, "location");
        var managerId = GetString(managerEmpInfo, "personIdExternal") ?? GetString(jobInfo, "managerId") ?? GetString(worker, "managerId");
        var employmentStatus = GetString(jobInfo, "emplStatus");
        var endDate = GetString(employment, "endDate");
        var firstDateWorked = GetString(employment, "firstDateWorked");
        var lastDateWorked = GetString(employment, "lastDateWorked");
        var isContingentWorker = GetString(employment, "isContingentWorker");

        var lifecycleState = MockLifecycleState.Infer(startDate, employmentStatus, endDate);
        var tags = InferScenarioTags(startDate, department, managerId, lifecycleState);
        return new MockWorkerFixture(
            PersonIdExternal: GetString(worker, "personIdExternal") ?? Guid.NewGuid().ToString("N"),
            UserName: GetString(userNav, "username") ?? GetString(worker, "username") ?? GetString(worker, "userName") ?? "user",
            Email: GetString(email, "emailAddress") ?? GetString(worker, "email") ?? "user@example.com",
            FirstName: GetString(personalInfo, "firstName") ?? GetString(worker, "firstName") ?? "Unknown",
            LastName: GetString(personalInfo, "lastName") ?? GetString(worker, "lastName") ?? "Worker",
            StartDate: startDate,
            Department: department,
            Company: company,
            Location: new MockLocationFixture(
                Name: locationName,
                Address: GetString(address, "address1") ?? GetString(location, "officeLocationAddress"),
                City: GetString(address, "city") ?? GetString(location, "officeLocationCity"),
                ZipCode: GetString(address, "zipCode") ?? GetString(location, "officeLocationZipCode"),
                CustomString4: GetString(address, "customString4")),
            JobTitle: GetString(jobInfo, "jobTitle"),
            BusinessUnit: GetString(businessUnitNav, "name_localized") ?? GetString(businessUnitNav, "businessUnit") ?? GetString(jobInfo, "businessUnit"),
            Division: GetString(divisionNav, "name_localized") ?? GetString(divisionNav, "division") ?? GetString(jobInfo, "division"),
            CostCenter: GetString(costCenterNav, "name_localized") ?? GetString(costCenterNav, "costCenterDescription") ?? GetString(jobInfo, "costCenter"),
            EmployeeClass: GetString(jobInfo, "employeeClass"),
            EmployeeType: GetString(jobInfo, "employeeType"),
            ManagerId: managerId,
            PeopleGroup: GetString(jobInfo, "customString3"),
            LeadershipLevel: GetString(jobInfo, "customString20"),
            Region: GetString(jobInfo, "customString87"),
            Geozone: GetString(jobInfo, "customString110"),
            BargainingUnit: GetString(jobInfo, "customString111"),
            UnionJobCode: GetString(jobInfo, "customString91"),
            CintasUniformCategory: GetString(jobInfo, "customString112"),
            CintasUniformAllotment: GetString(jobInfo, "customString113"),
            EmploymentStatus: employmentStatus,
            LifecycleState: lifecycleState,
            EndDate: endDate,
            FirstDateWorked: firstDateWorked,
            LastDateWorked: lastDateWorked,
            IsContingentWorker: isContingentWorker,
            LastModifiedDateTime: GetString(worker, "lastModifiedDateTime"),
            ScenarioTags: tags,
            Response: null,
            PersonId: GetString(worker, "personId"),
            PerPersonUuid: GetString(worker, "perPersonUuid"),
            PreferredName: GetString(personalInfo, "preferredName"),
            DisplayName: GetString(personalInfo, "displayName"),
            UserId: GetString(employment, "userId") ?? GetString(worker, "userId"),
            EmailType: GetString(email, "emailType"),
            DepartmentName: GetString(departmentNav, "name") ?? department,
            DepartmentId: GetString(departmentNav, "externalCode"),
            DepartmentCostCenter: GetString(departmentNav, "costCenter"),
            CompanyId: GetString(companyNav, "externalCode"),
            BusinessUnitId: GetString(businessUnitNav, "externalCode"),
            DivisionId: GetString(divisionNav, "externalCode"),
            CostCenterDescription: GetString(costCenterNav, "description_localized") ?? GetString(costCenterNav, "costCenterDescription"),
            CostCenterId: GetString(costCenterNav, "externalCode"),
            TwoCharCountryCode: GetString(companyCountryNav, "twoCharCountryCode"),
            Position: GetString(jobInfo, "position"),
            PayGrade: GetString(payGradeNav, "name"),
            BusinessPhoneNumber: GetString(businessPhone, "phoneNumber"),
            BusinessPhoneAreaCode: GetString(businessPhone, "areaCode"),
            BusinessPhoneCountryCode: GetString(businessPhone, "countryCode"),
            BusinessPhoneExtension: GetString(businessPhone, "extension"),
            CellPhoneNumber: GetString(cellPhone, "phoneNumber"),
            CellPhoneAreaCode: GetString(cellPhone, "areaCode"),
            CellPhoneCountryCode: GetString(cellPhone, "countryCode"),
            ActiveEmploymentsCount: GetString(personEmpTerminationInfoNav, "activeEmploymentsCount"),
            LatestTerminationDate: GetString(personEmpTerminationInfoNav, "latestTerminationDate"));
    }

    private static MockWorkerFixture SanitizeFixture(MockWorkerFixture worker, int index)
    {
        var key = worker.PersonIdExternal;
        var personNumber = StableNumber(key, 10_000, 99_999).ToString("D5");
        var sanitizedId = personNumber;
        var sequence = int.Parse(personNumber) - 10_000;
        var nameProfile = MockNameCatalog.GetNameProfile(sequence, worker.PreferredName is not null);
        var userName = $"user.{personNumber}";
        var email = $"{userName}@example.test";
        var managerId = string.IsNullOrWhiteSpace(worker.ManagerId)
            ? null
            : StableNumber(worker.ManagerId, 10_000, 99_999).ToString("D5");

        return worker with
        {
            PersonIdExternal = sanitizedId,
            PersonId = personNumber,
            PerPersonUuid = $"uuid-{personNumber}",
            UserName = userName,
            Email = email,
            FirstName = nameProfile.FirstName,
            LastName = nameProfile.LastName,
            PreferredName = nameProfile.PreferredName,
            DisplayName = worker.DisplayName is null ? null : nameProfile.DisplayName,
            UserId = string.IsNullOrWhiteSpace(worker.UserId) ? userName : userName,
            EmailType = worker.EmailType is null ? null : "B",
            ManagerId = managerId,
            Department = AliasDimension("Department", worker.Department),
            DepartmentName = AliasDimension("DepartmentName", worker.DepartmentName ?? worker.Department),
            DepartmentId = BuildCode("DEPT", worker.DepartmentId ?? worker.Department),
            DepartmentCostCenter = BuildCode("DCC", worker.DepartmentCostCenter ?? worker.CostCenter),
            Company = AliasDimension("Company", worker.Company),
            CompanyId = BuildCode("COMP", worker.CompanyId ?? worker.Company),
            BusinessUnit = AliasDimension("BusinessUnit", worker.BusinessUnit),
            BusinessUnitId = BuildCode("BU", worker.BusinessUnitId ?? worker.BusinessUnit),
            Division = AliasDimension("Division", worker.Division),
            DivisionId = BuildCode("DIV", worker.DivisionId ?? worker.Division),
            CostCenter = AliasDimension("CostCenter", worker.CostCenter),
            CostCenterDescription = AliasDimension("CostCenterDescription", worker.CostCenterDescription ?? worker.CostCenter),
            CostCenterId = BuildCode("CC", worker.CostCenterId ?? worker.CostCenter),
            JobTitle = AliasDimension("Job", worker.JobTitle),
            PeopleGroup = AliasDimension("PeopleGroup", worker.PeopleGroup),
            LeadershipLevel = AliasDimension("LeadershipLevel", worker.LeadershipLevel),
            Region = AliasDimension("Region", worker.Region),
            Geozone = AliasDimension("Geozone", worker.Geozone),
            BargainingUnit = AliasDimension("BargainingUnit", worker.BargainingUnit),
            UnionJobCode = AliasDimension("UnionJobCode", worker.UnionJobCode),
            CintasUniformCategory = AliasDimension("UniformCategory", worker.CintasUniformCategory),
            CintasUniformAllotment = AliasDimension("UniformAllotment", worker.CintasUniformAllotment),
            TwoCharCountryCode = string.IsNullOrWhiteSpace(worker.TwoCharCountryCode) ? null : "US",
            Position = AliasDimension("Position", worker.Position),
            PayGrade = AliasDimension("PayGrade", worker.PayGrade),
            BusinessPhoneNumber = worker.BusinessPhoneNumber is null ? null : $"{StableNumber(key + ":bpn", 5550000, 5559999):D7}",
            BusinessPhoneAreaCode = worker.BusinessPhoneAreaCode is null ? null : $"{StableNumber(key + ":bpa", 200, 999):D3}",
            BusinessPhoneCountryCode = worker.BusinessPhoneCountryCode is null ? null : "1",
            BusinessPhoneExtension = worker.BusinessPhoneExtension is null ? null : $"{StableNumber(key + ":bpe", 100, 999):D3}",
            CellPhoneNumber = worker.CellPhoneNumber is null ? null : $"{StableNumber(key + ":cpn", 5550000, 5559999):D7}",
            CellPhoneAreaCode = worker.CellPhoneAreaCode is null ? null : $"{StableNumber(key + ":cpa", 200, 999):D3}",
            CellPhoneCountryCode = worker.CellPhoneCountryCode is null ? null : "1",
            ActiveEmploymentsCount = string.IsNullOrWhiteSpace(worker.ActiveEmploymentsCount) ? null : worker.ActiveEmploymentsCount,
            LatestTerminationDate = string.IsNullOrWhiteSpace(worker.LatestTerminationDate) ? null : worker.LatestTerminationDate,
            Location = worker.Location is null
                ? null
                : new MockLocationFixture(
                    Name: AliasDimension("Location", worker.Location.Name),
                    Address: worker.Location.Address is null ? null : $"Suite {StableNumber(key + ":addr", 100, 999)} Example Way",
                    City: worker.Location.City is null ? null : $"City{StableNumber(key + ":city", 10, 99)}",
                    ZipCode: worker.Location.ZipCode is null ? null : $"{StableNumber(key + ":zip", 10_000, 99_999):D5}",
                    CustomString4: worker.Location.CustomString4 is null ? null : $"Floor {StableNumber(key + ":floor", 1, 20)}"),
            LifecycleState = MockLifecycleState.Normalize(worker.LifecycleState),
            ScenarioTags = worker.ScenarioTags.Count > 0
                ? worker.ScenarioTags
                : InferDefaultScenarioTags(index, worker.StartDate, MockLifecycleState.Normalize(worker.LifecycleState))
        };
    }

    private static IReadOnlyList<string> InferScenarioTags(string startDate, string? department, string? managerId, string lifecycleState)
    {
        var tags = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "update" };
        if (string.Equals(lifecycleState, MockLifecycleState.Preboarding, StringComparison.OrdinalIgnoreCase))
        {
            tags.Add("preboarding");
            tags.Add("prehire");
        }

        if (!string.IsNullOrWhiteSpace(department))
        {
            tags.Add("ou-routing");
        }

        if (!string.IsNullOrWhiteSpace(managerId))
        {
            tags.Add("manager-change");
        }

        switch (lifecycleState)
        {
            case MockLifecycleState.PaidLeave:
            case MockLifecycleState.UnpaidLeave:
                tags.Add("leave");
                tags.Add("disable-candidate");
                break;
            case MockLifecycleState.Retired:
            case MockLifecycleState.Terminated:
                tags.Add("inactive");
                tags.Add("delete-candidate");
                break;
        }

        return tags.ToArray();
    }

    private static IReadOnlyList<string> InferDefaultScenarioTags(int index, string startDate, string lifecycleState)
    {
        var tags = new List<string> { index == 0 ? "create" : "update" };
        if (string.Equals(lifecycleState, MockLifecycleState.Preboarding, StringComparison.OrdinalIgnoreCase) ||
            DateTimeOffset.TryParse(startDate, out var parsedStart) && parsedStart > DateTimeOffset.UtcNow)
        {
            tags.Add("preboarding");
            tags.Add("prehire");
        }

        switch (lifecycleState)
        {
            case MockLifecycleState.PaidLeave:
            case MockLifecycleState.UnpaidLeave:
                tags.Add("leave");
                tags.Add("disable-candidate");
                break;
            case MockLifecycleState.Retired:
            case MockLifecycleState.Terminated:
                tags.Add("inactive");
                tags.Add("delete-candidate");
                break;
        }

        return tags;
    }

    private static int StableNumber(string input, int min, int max)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(input));
        var value = BitConverter.ToUInt32(hash, 0);
        return min + (int)(value % (uint)(max - min + 1));
    }

    private static string? AliasDimension(string dimension, string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return value;
        }

        return $"{dimension}-{StableNumber($"{dimension}:{value}", 1, 99):D2}";
    }

    private static string? BuildCode(string prefix, string? seed)
    {
        if (string.IsNullOrWhiteSpace(seed))
        {
            return null;
        }

        return $"{prefix}-{StableNumber($"{prefix}:{seed}", 100, 999)}";
    }

    private static JsonElement? GetFirstResult(JsonElement element, string propertyName)
    {
        if (element.TryGetProperty(propertyName, out var navigation) &&
            navigation.ValueKind == JsonValueKind.Object &&
            navigation.TryGetProperty("results", out var results) &&
            results.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in results.EnumerateArray())
            {
                return item.Clone();
            }
        }

        return null;
    }

    private static JsonElement? GetFirstResult(JsonElement? element, string propertyName)
    {
        return element is { ValueKind: not JsonValueKind.Undefined } ? GetFirstResult(element.Value, propertyName) : null;
    }

    private static JsonElement? GetPreferredResult(JsonElement element, string propertyName, Func<JsonElement, bool> predicate)
    {
        if (element.TryGetProperty(propertyName, out var navigation) &&
            navigation.ValueKind == JsonValueKind.Object &&
            navigation.TryGetProperty("results", out var results) &&
            results.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in results.EnumerateArray())
            {
                if (predicate(item))
                {
                    return item.Clone();
                }
            }
        }

        return null;
    }

    private static JsonElement? GetObject(JsonElement element, string propertyName)
    {
        return element.TryGetProperty(propertyName, out var property) && property.ValueKind == JsonValueKind.Object
            ? property.Clone()
            : null;
    }

    private static JsonElement? GetObject(JsonElement? element, string propertyName)
    {
        return element is { ValueKind: not JsonValueKind.Undefined } ? GetObject(element.Value, propertyName) : null;
    }

    private static string? GetString(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var property))
        {
            return null;
        }

        return property.ValueKind switch
        {
            JsonValueKind.String => property.GetString(),
            JsonValueKind.Number => property.ToString(),
            JsonValueKind.True => bool.TrueString.ToLowerInvariant(),
            JsonValueKind.False => bool.FalseString.ToLowerInvariant(),
            _ => null
        };
    }

    private static string? GetString(JsonElement? element, string propertyName)
    {
        return element is { ValueKind: not JsonValueKind.Undefined } ? GetString(element.Value, propertyName) : null;
    }

    private static bool IsTrue(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var property))
        {
            return false;
        }

        return property.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.String => bool.TryParse(property.GetString(), out var parsed) && parsed,
            _ => false
        };
    }

    private static bool PropertyEquals(JsonElement element, string propertyName, string expectedValue)
    {
        return string.Equals(GetString(element, propertyName), expectedValue, StringComparison.OrdinalIgnoreCase);
    }
}

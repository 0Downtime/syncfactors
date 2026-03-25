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
        var email = GetFirstResult(worker, "emailNav");
        var location = jobInfo is { ValueKind: not JsonValueKind.Undefined } ? GetObject(jobInfo.Value, "locationNav") : null;

        var startDate = GetString(employment, "startDate") ?? GetString(worker, "startDate") ?? DateTimeOffset.UtcNow.ToString("O");
        var department = GetString(GetObject(jobInfo, "departmentNav"), "department") ?? GetString(jobInfo, "department");
        var company = GetString(GetObject(jobInfo, "companyNav"), "company") ?? GetString(jobInfo, "company");
        var locationName = GetString(location, "LocationName") ?? GetString(jobInfo, "location");
        var managerId = GetString(jobInfo, "managerId") ?? GetString(worker, "managerId");

        var tags = InferScenarioTags(startDate, department, managerId);
        return new MockWorkerFixture(
            PersonIdExternal: GetString(worker, "personIdExternal") ?? Guid.NewGuid().ToString("N"),
            UserName: GetString(worker, "username") ?? GetString(worker, "userName") ?? "user",
            Email: GetString(email, "emailAddress") ?? GetString(worker, "email") ?? "user@example.com",
            FirstName: GetString(personalInfo, "firstName") ?? GetString(worker, "firstName") ?? "Unknown",
            LastName: GetString(personalInfo, "lastName") ?? GetString(worker, "lastName") ?? "Worker",
            StartDate: startDate,
            Department: department,
            Company: company,
            Location: new MockLocationFixture(
                Name: locationName,
                Address: GetString(location, "officeLocationAddress"),
                City: GetString(location, "officeLocationCity"),
                ZipCode: GetString(location, "officeLocationZipCode")),
            JobTitle: GetString(jobInfo, "jobTitle"),
            BusinessUnit: GetString(GetObject(jobInfo, "businessUnitNav"), "businessUnit") ?? GetString(jobInfo, "businessUnit"),
            Division: GetString(GetObject(jobInfo, "divisionNav"), "division") ?? GetString(jobInfo, "division"),
            CostCenter: GetString(GetObject(jobInfo, "costCenterNav"), "costCenterDescription") ?? GetString(jobInfo, "costCenter"),
            EmployeeClass: GetString(jobInfo, "employeeClass"),
            EmployeeType: GetString(jobInfo, "employeeType"),
            ManagerId: managerId,
            EmploymentStatus: GetString(jobInfo, "emplStatus"),
            LastModifiedDateTime: GetString(worker, "lastModifiedDateTime"),
            ScenarioTags: tags,
            Response: null);
    }

    private static MockWorkerFixture SanitizeFixture(MockWorkerFixture worker, int index)
    {
        var key = worker.PersonIdExternal;
        var personNumber = StableNumber(key, 10_000, 99_999).ToString("D5");
        var sanitizedId = $"mock-{personNumber}";
        var firstName = $"Worker{StableNumber(key + ":fn", 10, 999):D3}";
        var lastName = $"Sample{StableNumber(key + ":ln", 10, 999):D3}";
        var userName = $"user.{personNumber}";
        var email = $"{userName}@example.test";
        var managerId = string.IsNullOrWhiteSpace(worker.ManagerId)
            ? null
            : $"mock-{StableNumber(worker.ManagerId, 10_000, 99_999):D5}";

        return worker with
        {
            PersonIdExternal = sanitizedId,
            UserName = userName,
            Email = email,
            FirstName = firstName,
            LastName = lastName,
            ManagerId = managerId,
            Department = AliasDimension("Department", worker.Department),
            Company = AliasDimension("Company", worker.Company),
            BusinessUnit = AliasDimension("BusinessUnit", worker.BusinessUnit),
            Division = AliasDimension("Division", worker.Division),
            CostCenter = AliasDimension("CostCenter", worker.CostCenter),
            JobTitle = AliasDimension("Job", worker.JobTitle),
            Location = worker.Location is null
                ? null
                : new MockLocationFixture(
                    Name: AliasDimension("Location", worker.Location.Name),
                    Address: worker.Location.Address is null ? null : $"Suite {StableNumber(key + ":addr", 100, 999)} Example Way",
                    City: worker.Location.City is null ? null : $"City{StableNumber(key + ":city", 10, 99)}",
                    ZipCode: worker.Location.ZipCode is null ? null : $"{StableNumber(key + ":zip", 10_000, 99_999):D5}"),
            ScenarioTags = worker.ScenarioTags.Count > 0 ? worker.ScenarioTags : InferDefaultScenarioTags(index, worker.StartDate)
        };
    }

    private static IReadOnlyList<string> InferScenarioTags(string startDate, string? department, string? managerId)
    {
        var tags = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "update" };
        if (DateTimeOffset.TryParse(startDate, out var parsedStart) && parsedStart > DateTimeOffset.UtcNow)
        {
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

        return tags.ToArray();
    }

    private static IReadOnlyList<string> InferDefaultScenarioTags(int index, string startDate)
    {
        var tags = new List<string> { index == 0 ? "create" : "update" };
        if (DateTimeOffset.TryParse(startDate, out var parsedStart) && parsedStart > DateTimeOffset.UtcNow)
        {
            tags.Add("prehire");
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
        return element.TryGetProperty(propertyName, out var property) && property.ValueKind == JsonValueKind.String
            ? property.GetString()
            : null;
    }

    private static string? GetString(JsonElement? element, string propertyName)
    {
        return element is { ValueKind: not JsonValueKind.Undefined } ? GetString(element.Value, propertyName) : null;
    }
}

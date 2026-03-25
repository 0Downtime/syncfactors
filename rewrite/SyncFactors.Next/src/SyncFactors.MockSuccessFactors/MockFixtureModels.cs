using System.Text.Json.Serialization;

namespace SyncFactors.MockSuccessFactors;

public sealed record MockFixtureDocument(
    [property: JsonPropertyName("workers")] IReadOnlyList<MockWorkerFixture> Workers);

public sealed record MockWorkerFixture(
    [property: JsonPropertyName("personIdExternal")] string PersonIdExternal,
    [property: JsonPropertyName("userName")] string UserName,
    [property: JsonPropertyName("email")] string Email,
    [property: JsonPropertyName("firstName")] string FirstName,
    [property: JsonPropertyName("lastName")] string LastName,
    [property: JsonPropertyName("startDate")] string StartDate,
    [property: JsonPropertyName("department")] string? Department,
    [property: JsonPropertyName("company")] string? Company,
    [property: JsonPropertyName("location")] MockLocationFixture? Location,
    [property: JsonPropertyName("jobTitle")] string? JobTitle,
    [property: JsonPropertyName("businessUnit")] string? BusinessUnit,
    [property: JsonPropertyName("division")] string? Division,
    [property: JsonPropertyName("costCenter")] string? CostCenter,
    [property: JsonPropertyName("employeeClass")] string? EmployeeClass,
    [property: JsonPropertyName("employeeType")] string? EmployeeType,
    [property: JsonPropertyName("managerId")] string? ManagerId,
    [property: JsonPropertyName("employmentStatus")] string? EmploymentStatus,
    [property: JsonPropertyName("lastModifiedDateTime")] string? LastModifiedDateTime,
    [property: JsonPropertyName("scenarioTags")] IReadOnlyList<string> ScenarioTags,
    [property: JsonPropertyName("response")] MockWorkerResponseControls? Response);

public sealed record MockLocationFixture(
    [property: JsonPropertyName("name")] string? Name,
    [property: JsonPropertyName("address")] string? Address,
    [property: JsonPropertyName("city")] string? City,
    [property: JsonPropertyName("zipCode")] string? ZipCode);

public sealed record MockWorkerResponseControls(
    [property: JsonPropertyName("forceUnauthorized")] bool ForceUnauthorized,
    [property: JsonPropertyName("forceNotFound")] bool ForceNotFound,
    [property: JsonPropertyName("forceMalformedPayload")] bool ForceMalformedPayload,
    [property: JsonPropertyName("forceEmptyResults")] bool ForceEmptyResults);

public sealed record TokenResponse(
    [property: JsonPropertyName("access_token")] string AccessToken,
    [property: JsonPropertyName("token_type")] string TokenType,
    [property: JsonPropertyName("expires_in")] int ExpiresIn);

public sealed record FixtureManifest(
    [property: JsonPropertyName("sourcePath")] string SourcePath,
    [property: JsonPropertyName("generatedAtUtc")] string GeneratedAtUtc,
    [property: JsonPropertyName("sanitizationProfile")] string SanitizationProfile,
    [property: JsonPropertyName("workerCount")] int WorkerCount,
    [property: JsonPropertyName("scenarioCounts")] IReadOnlyDictionary<string, int> ScenarioCounts);

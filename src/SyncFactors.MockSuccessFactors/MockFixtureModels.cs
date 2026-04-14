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
    [property: JsonPropertyName("peopleGroup")] string? PeopleGroup,
    [property: JsonPropertyName("leadershipLevel")] string? LeadershipLevel,
    [property: JsonPropertyName("region")] string? Region,
    [property: JsonPropertyName("geozone")] string? Geozone,
    [property: JsonPropertyName("bargainingUnit")] string? BargainingUnit,
    [property: JsonPropertyName("unionJobCode")] string? UnionJobCode,
    [property: JsonPropertyName("cintasUniformCategory")] string? CintasUniformCategory,
    [property: JsonPropertyName("cintasUniformAllotment")] string? CintasUniformAllotment,
    [property: JsonPropertyName("employmentStatus")] string? EmploymentStatus,
    [property: JsonPropertyName("lifecycleState")] string? LifecycleState,
    [property: JsonPropertyName("endDate")] string? EndDate,
    [property: JsonPropertyName("firstDateWorked")] string? FirstDateWorked,
    [property: JsonPropertyName("lastDateWorked")] string? LastDateWorked,
    [property: JsonPropertyName("isContingentWorker")] string? IsContingentWorker,
    [property: JsonPropertyName("lastModifiedDateTime")] string? LastModifiedDateTime,
    [property: JsonPropertyName("scenarioTags")] IReadOnlyList<string> ScenarioTags,
    [property: JsonPropertyName("response")] MockWorkerResponseControls? Response,
    [property: JsonPropertyName("personId")] string? PersonId = null,
    [property: JsonPropertyName("perPersonUuid")] string? PerPersonUuid = null,
    [property: JsonPropertyName("preferredName")] string? PreferredName = null,
    [property: JsonPropertyName("displayName")] string? DisplayName = null,
    [property: JsonPropertyName("userId")] string? UserId = null,
    [property: JsonPropertyName("emailType")] string? EmailType = null,
    [property: JsonPropertyName("departmentName")] string? DepartmentName = null,
    [property: JsonPropertyName("departmentId")] string? DepartmentId = null,
    [property: JsonPropertyName("departmentCostCenter")] string? DepartmentCostCenter = null,
    [property: JsonPropertyName("companyId")] string? CompanyId = null,
    [property: JsonPropertyName("businessUnitId")] string? BusinessUnitId = null,
    [property: JsonPropertyName("divisionId")] string? DivisionId = null,
    [property: JsonPropertyName("costCenterDescription")] string? CostCenterDescription = null,
    [property: JsonPropertyName("costCenterId")] string? CostCenterId = null,
    [property: JsonPropertyName("twoCharCountryCode")] string? TwoCharCountryCode = null,
    [property: JsonPropertyName("position")] string? Position = null,
    [property: JsonPropertyName("payGrade")] string? PayGrade = null,
    [property: JsonPropertyName("businessPhoneNumber")] string? BusinessPhoneNumber = null,
    [property: JsonPropertyName("businessPhoneAreaCode")] string? BusinessPhoneAreaCode = null,
    [property: JsonPropertyName("businessPhoneCountryCode")] string? BusinessPhoneCountryCode = null,
    [property: JsonPropertyName("businessPhoneExtension")] string? BusinessPhoneExtension = null,
    [property: JsonPropertyName("cellPhoneNumber")] string? CellPhoneNumber = null,
    [property: JsonPropertyName("cellPhoneAreaCode")] string? CellPhoneAreaCode = null,
    [property: JsonPropertyName("cellPhoneCountryCode")] string? CellPhoneCountryCode = null,
    [property: JsonPropertyName("activeEmploymentsCount")] string? ActiveEmploymentsCount = null,
    [property: JsonPropertyName("latestTerminationDate")] string? LatestTerminationDate = null);

public sealed record MockLocationFixture(
    [property: JsonPropertyName("name")] string? Name,
    [property: JsonPropertyName("address")] string? Address,
    [property: JsonPropertyName("city")] string? City,
    [property: JsonPropertyName("zipCode")] string? ZipCode,
    [property: JsonPropertyName("customString4")] string? CustomString4 = null);

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

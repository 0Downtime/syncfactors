using System.Text.Json.Serialization;

namespace SyncFactors.MockSuccessFactors;

public sealed record MockAdminStateResponse(
    [property: JsonPropertyName("sourceFixturePath")] string SourceFixturePath,
    [property: JsonPropertyName("runtimeFixturePath")] string RuntimeFixturePath,
    [property: JsonPropertyName("adminPath")] string AdminPath,
    [property: JsonPropertyName("totalWorkers")] int TotalWorkers,
    [property: JsonPropertyName("filteredWorkers")] int FilteredWorkers,
    [property: JsonPropertyName("provisioningBuckets")] IReadOnlyList<MockAdminBucketCount> ProvisioningBuckets,
    [property: JsonPropertyName("workers")] IReadOnlyList<MockAdminWorkerSummary> Workers);

public sealed record MockAdminBucketCount(
    [property: JsonPropertyName("bucket")] string Bucket,
    [property: JsonPropertyName("label")] string Label,
    [property: JsonPropertyName("count")] int Count);

public sealed record MockAdminWorkerSummary(
    [property: JsonPropertyName("personIdExternal")] string PersonIdExternal,
    [property: JsonPropertyName("userId")] string UserId,
    [property: JsonPropertyName("displayName")] string DisplayName,
    [property: JsonPropertyName("email")] string Email,
    [property: JsonPropertyName("employmentStatus")] string EmploymentStatus,
    [property: JsonPropertyName("lifecycleState")] string LifecycleState,
    [property: JsonPropertyName("company")] string? Company,
    [property: JsonPropertyName("department")] string? Department,
    [property: JsonPropertyName("managerId")] string? ManagerId,
    [property: JsonPropertyName("scenarioTags")] IReadOnlyList<string> ScenarioTags,
    [property: JsonPropertyName("provisioningBucket")] string ProvisioningBucket,
    [property: JsonPropertyName("provisioningBucketLabel")] string ProvisioningBucketLabel);

public sealed record MockAdminWorkerDetailResponse(
    [property: JsonPropertyName("worker")] MockAdminWorkerUpsertRequest Worker,
    [property: JsonPropertyName("mode")] string Mode,
    [property: JsonPropertyName("bucketComparison")] MockAdminBucketComparison? BucketComparison);

public sealed record MockAdminWorkerMutationResponse(
    [property: JsonPropertyName("message")] string Message,
    [property: JsonPropertyName("worker")] MockAdminWorkerUpsertRequest Worker);

public sealed record MockAdminBucketComparison(
    [property: JsonPropertyName("mockBucket")] MockAdminBucketSnapshot MockBucket,
    [property: JsonPropertyName("plannerBucket")] MockAdminPlannerBucketSnapshot PlannerBucket);

public sealed record MockAdminBucketSnapshot(
    [property: JsonPropertyName("bucket")] string Bucket,
    [property: JsonPropertyName("label")] string Label);

public sealed record MockAdminPlannerBucketSnapshot(
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("bucket")] string? Bucket,
    [property: JsonPropertyName("label")] string? Label,
    [property: JsonPropertyName("reason")] string? Reason,
    [property: JsonPropertyName("reviewCaseType")] string? ReviewCaseType,
    [property: JsonPropertyName("error")] string? Error);

public sealed record MockAdminResetResponse(
    [property: JsonPropertyName("message")] string Message,
    [property: JsonPropertyName("workerCount")] int WorkerCount);

public sealed record MockAdminCloneRequest(
    [property: JsonPropertyName("sourceWorkerId")] string? SourceWorkerId);

public sealed record MockAdminLifecycleStateRequest(
    [property: JsonPropertyName("lifecycleState")] string? LifecycleState);

public sealed record MockAdminWorkerUpsertRequest(
    [property: JsonPropertyName("personIdExternal")] string? PersonIdExternal = null,
    [property: JsonPropertyName("userName")] string? UserName = null,
    [property: JsonPropertyName("email")] string? Email = null,
    [property: JsonPropertyName("firstName")] string? FirstName = null,
    [property: JsonPropertyName("lastName")] string? LastName = null,
    [property: JsonPropertyName("startDate")] string? StartDate = null,
    [property: JsonPropertyName("department")] string? Department = null,
    [property: JsonPropertyName("company")] string? Company = null,
    [property: JsonPropertyName("location")] MockAdminLocationInput? Location = null,
    [property: JsonPropertyName("jobTitle")] string? JobTitle = null,
    [property: JsonPropertyName("businessUnit")] string? BusinessUnit = null,
    [property: JsonPropertyName("division")] string? Division = null,
    [property: JsonPropertyName("costCenter")] string? CostCenter = null,
    [property: JsonPropertyName("employeeClass")] string? EmployeeClass = null,
    [property: JsonPropertyName("employeeType")] string? EmployeeType = null,
    [property: JsonPropertyName("managerId")] string? ManagerId = null,
    [property: JsonPropertyName("peopleGroup")] string? PeopleGroup = null,
    [property: JsonPropertyName("leadershipLevel")] string? LeadershipLevel = null,
    [property: JsonPropertyName("region")] string? Region = null,
    [property: JsonPropertyName("geozone")] string? Geozone = null,
    [property: JsonPropertyName("bargainingUnit")] string? BargainingUnit = null,
    [property: JsonPropertyName("unionJobCode")] string? UnionJobCode = null,
    [property: JsonPropertyName("cintasUniformCategory")] string? CintasUniformCategory = null,
    [property: JsonPropertyName("cintasUniformAllotment")] string? CintasUniformAllotment = null,
    [property: JsonPropertyName("employmentStatus")] string? EmploymentStatus = null,
    [property: JsonPropertyName("lifecycleState")] string? LifecycleState = null,
    [property: JsonPropertyName("endDate")] string? EndDate = null,
    [property: JsonPropertyName("firstDateWorked")] string? FirstDateWorked = null,
    [property: JsonPropertyName("lastDateWorked")] string? LastDateWorked = null,
    [property: JsonPropertyName("isContingentWorker")] string? IsContingentWorker = null,
    [property: JsonPropertyName("lastModifiedDateTime")] string? LastModifiedDateTime = null,
    [property: JsonPropertyName("scenarioTags")] IReadOnlyList<string>? ScenarioTags = null,
    [property: JsonPropertyName("response")] MockAdminResponseControlsInput? Response = null,
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

public sealed record MockAdminLocationInput(
    [property: JsonPropertyName("name")] string? Name,
    [property: JsonPropertyName("address")] string? Address,
    [property: JsonPropertyName("city")] string? City,
    [property: JsonPropertyName("zipCode")] string? ZipCode,
    [property: JsonPropertyName("customString4")] string? CustomString4 = null);

public sealed record MockAdminResponseControlsInput(
    [property: JsonPropertyName("forceUnauthorized")] bool ForceUnauthorized,
    [property: JsonPropertyName("forceNotFound")] bool ForceNotFound,
    [property: JsonPropertyName("forceMalformedPayload")] bool ForceMalformedPayload,
    [property: JsonPropertyName("forceEmptyResults")] bool ForceEmptyResults);

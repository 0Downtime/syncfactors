using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using SyncFactors.MockSuccessFactors;

namespace SyncFactors.MockSuccessFactors.Tests;

public sealed class MockApiTests
{
    private static readonly string FixturePath = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "config", "mock-successfactors", "baseline-fixtures.json"));

    private static MockFixtureStore CreateStore(bool syntheticPopulationEnabled = false, int targetWorkerCount = 5000)
    {
        return new MockFixtureStore(Options.Create(new MockSuccessFactorsOptions
        {
            FixturePath = FixturePath,
            SyntheticPopulation = new MockSyntheticPopulationOptions
            {
                Enabled = syntheticPopulationEnabled,
                TargetWorkerCount = targetWorkerCount
            }
        }));
    }

    [Fact]
    public void TokenService_IssuesBearerToken_ForValidClientCredentials()
    {
        var service = new MockTokenService();
        var token = service.IssueToken("client_credentials", "mock-client-id", "mock-client-secret", "MOCK", new MockAuthenticationOptions());

        Assert.NotNull(token);
        Assert.Equal("mock-access-token", token!.AccessToken);
    }

    [Fact]
    public void TokenService_RejectsInvalidClientCredentials()
    {
        var service = new MockTokenService();
        var token = service.IssueToken("client_credentials", "bad", "bad", "MOCK", new MockAuthenticationOptions());

        Assert.Null(token);
    }

    [Fact]
    public void PerPersonProjection_ReturnsProjectedWorker_ForCurrentQueryShape()
    {
        var store = CreateStore();
        var builder = new ODataResponseBuilder();
        var query = ODataQueryParser.Parse(new Microsoft.AspNetCore.Http.QueryCollection(new Dictionary<string, Microsoft.Extensions.Primitives.StringValues>
        {
            ["$format"] = "json",
            ["$filter"] = "personIdExternal eq '10001'",
            ["$select"] = "personIdExternal,personalInfoNav/firstName,personalInfoNav/lastName,employmentNav/startDate,emailNav/emailAddress,employmentNav/jobInfoNav/departmentNav/department,employmentNav/jobInfoNav/companyNav/company,employmentNav/jobInfoNav/locationNav/LocationName,employmentNav/jobInfoNav/jobTitle,employmentNav/jobInfoNav/businessUnitNav/businessUnit,employmentNav/jobInfoNav/divisionNav/division,employmentNav/jobInfoNav/costCenterNav/costCenterDescription,employmentNav/jobInfoNav/employeeClass,employmentNav/jobInfoNav/employeeType,employmentNav/jobInfoNav/managerId,employmentNav/jobInfoNav/customString3,employmentNav/jobInfoNav/customString20,employmentNav/jobInfoNav/customString87,employmentNav/jobInfoNav/customString110,employmentNav/jobInfoNav/customString111,employmentNav/jobInfoNav/customString91",
            ["$expand"] = "employmentNav,employmentNav/jobInfoNav,personalInfoNav,emailNav,employmentNav/jobInfoNav/companyNav,employmentNav/jobInfoNav/departmentNav,employmentNav/jobInfoNav/businessUnitNav,employmentNav/jobInfoNav/costCenterNav,employmentNav/jobInfoNav/divisionNav,employmentNav/jobInfoNav/locationNav"
        }));

        var payload = builder.Build(store.FindByIdentity(query.IdentityField, query.WorkerId), query);
        using var document = JsonDocument.Parse(JsonSerializer.Serialize(payload));
        var worker = document.RootElement.GetProperty("d").GetProperty("results")[0];

        Assert.Equal("10001", worker.GetProperty("personIdExternal").GetString());
        Assert.Equal("Worker10001", worker.GetProperty("personalInfoNav").GetProperty("results")[0].GetProperty("firstName").GetString());
        Assert.Equal("CORP", worker.GetProperty("employmentNav").GetProperty("results")[0].GetProperty("jobInfoNav").GetProperty("results")[0].GetProperty("companyNav").GetProperty("company").GetString());
        Assert.Equal("Central", worker.GetProperty("employmentNav").GetProperty("results")[0].GetProperty("jobInfoNav").GetProperty("results")[0].GetProperty("customString87").GetString());
    }

    [Fact]
    public void PerPersonProjection_ReturnsEmptyResults_WhenWorkerDoesNotExist()
    {
        var store = CreateStore();
        var builder = new ODataResponseBuilder();
        var query = ODataQueryParser.Parse(new Microsoft.AspNetCore.Http.QueryCollection(new Dictionary<string, Microsoft.Extensions.Primitives.StringValues>
        {
            ["$format"] = "json",
            ["$filter"] = "personIdExternal eq 'missing'",
            ["$select"] = "personIdExternal",
            ["$expand"] = "employmentNav"
        }));

        var payload = builder.Build(store.FindByIdentity(query.IdentityField, query.WorkerId), query);
        using var document = JsonDocument.Parse(JsonSerializer.Serialize(payload));
        Assert.Equal(0, document.RootElement.GetProperty("d").GetProperty("results").GetArrayLength());
    }

    [Fact]
    public void EmpJobProjection_ReturnsProjectedWorker_ForTrackedRealQueryShape()
    {
        var store = CreateStore();
        var builder = new ODataResponseBuilder();
        var query = ODataQueryParser.Parse(new Microsoft.AspNetCore.Http.QueryCollection(new Dictionary<string, Microsoft.Extensions.Primitives.StringValues>
        {
            ["$format"] = "json",
            ["$top"] = "1",
            ["$filter"] = "userId eq 'user.10001'",
            ["$select"] = "userId,jobTitle,company,department,division,location,businessUnit,costCenter,employeeClass,employeeType,managerId,customString3,customString20,customString87,customString110,customString111,customString91,startDate",
            ["$expand"] = "companyNav,departmentNav,divisionNav,locationNav,businessUnitNav,costCenterNav"
        }));

        var payload = builder.Build(store.FindByIdentity(query.IdentityField, query.WorkerId), query, "EmpJob");
        using var document = JsonDocument.Parse(JsonSerializer.Serialize(payload));
        var job = document.RootElement.GetProperty("d").GetProperty("results")[0];

        Assert.Equal("user.10001", job.GetProperty("userId").GetString());
        Assert.Equal("CORP", job.GetProperty("company").GetString());
        Assert.Equal("CORP", job.GetProperty("companyNav").GetProperty("company").GetString());
        Assert.Equal("Central", job.GetProperty("customString87").GetString());
        Assert.False(job.TryGetProperty("personIdExternal", out _));
    }

    [Fact]
    public void SyntheticPopulation_IsOptIn_AndExpandsToRequestedWorkerCount()
    {
        var baselineStore = CreateStore();
        var syntheticStore = CreateStore(syntheticPopulationEnabled: true);

        var baselineWorkers = baselineStore.GetDocument().Workers;
        var syntheticWorkers = syntheticStore.GetDocument().Workers;

        Assert.Equal(7, baselineWorkers.Count);
        Assert.Equal(5000, syntheticWorkers.Count);
        Assert.Equal("10001", baselineWorkers[0].PersonIdExternal);
        Assert.Equal("Worker10001", baselineWorkers[0].FirstName);
        Assert.Equal("10000", syntheticWorkers[0].PersonIdExternal);
        Assert.Equal("14999", syntheticWorkers[^1].PersonIdExternal);
    }

    [Fact]
    public void FixtureNormalization_IsDeterministicAcrossLoads()
    {
        var firstLoad = CreateStore().GetDocument().Workers;
        var secondLoad = CreateStore().GetDocument().Workers;

        Assert.Equal(firstLoad.Count, secondLoad.Count);
        Assert.Equal(firstLoad.Select(worker => worker.PersonIdExternal), secondLoad.Select(worker => worker.PersonIdExternal));
        Assert.Equal(firstLoad[0].FirstName, secondLoad[0].FirstName);
        Assert.Equal(firstLoad[0].Email, secondLoad[0].Email);
        Assert.Equal(firstLoad[0].Location?.Address, secondLoad[0].Location?.Address);
        Assert.Equal("Worker10001", firstLoad[0].FirstName);
        Assert.Equal("user.10001@example.test", firstLoad[0].Email);
        Assert.Equal("Suite 10001", firstLoad[0].Location?.Address);
    }

    [Fact]
    public void SyntheticPopulation_GeneratesUniqueIdentities_AndPreservesLifecycleCoverage()
    {
        var workers = CreateStore(syntheticPopulationEnabled: true).GetDocument().Workers;

        Assert.Equal(5000, workers.Select(worker => worker.PersonIdExternal).Distinct(StringComparer.Ordinal).Count());
        Assert.Equal(5000, workers.Select(worker => worker.UserName).Distinct(StringComparer.Ordinal).Count());
        Assert.Equal(5000, workers.Select(worker => worker.UserId).Distinct(StringComparer.Ordinal).Count());
        Assert.Equal(5000, workers.Select(worker => worker.Email).Distinct(StringComparer.OrdinalIgnoreCase).Count());

        var workerIds = workers.Select(worker => worker.PersonIdExternal).ToHashSet(StringComparer.Ordinal);
        Assert.All(workers.Where(worker => !string.IsNullOrWhiteSpace(worker.ManagerId)), worker =>
        {
            Assert.Contains(worker.ManagerId!, workerIds);
        });

        var tags = workers
            .SelectMany(worker => worker.ScenarioTags)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        Assert.Contains("create", tags);
        Assert.Contains("update", tags);
        Assert.Contains("prehire", tags);
        Assert.Contains("manager-change", tags);
        Assert.Contains("disable-candidate", tags);
        Assert.Contains("delete-candidate", tags);
    }

    [Fact]
    public void SyntheticPopulation_PerPersonProjection_ResolvesWorkerNearEndOfRange()
    {
        var store = CreateStore(syntheticPopulationEnabled: true);
        var builder = new ODataResponseBuilder();
        var query = ODataQueryParser.Parse(new Microsoft.AspNetCore.Http.QueryCollection(new Dictionary<string, Microsoft.Extensions.Primitives.StringValues>
        {
            ["$format"] = "json",
            ["$filter"] = "personIdExternal eq '14999'",
            ["$select"] = "personIdExternal,personalInfoNav/firstName,employmentNav/userId,emailNav/emailAddress,employmentNav/jobInfoNav/managerId",
            ["$expand"] = "employmentNav,employmentNav/jobInfoNav,personalInfoNav,emailNav"
        }));

        var payload = builder.Build(store.FindByIdentity(query.IdentityField, query.WorkerId), query);
        using var document = JsonDocument.Parse(JsonSerializer.Serialize(payload));
        var worker = document.RootElement.GetProperty("d").GetProperty("results")[0];

        Assert.Equal("14999", worker.GetProperty("personIdExternal").GetString());
        Assert.Equal("Worker14999", worker.GetProperty("personalInfoNav").GetProperty("results")[0].GetProperty("firstName").GetString());
        Assert.Equal("user.14999", worker.GetProperty("employmentNav").GetProperty("results")[0].GetProperty("userId").GetString());
        Assert.Equal("user.14999@example.test", worker.GetProperty("emailNav").GetProperty("results")[0].GetProperty("emailAddress").GetString());
    }

    [Fact]
    public void SyntheticPopulation_EmpJobProjection_ResolvesWorkerNearEndOfRange()
    {
        var store = CreateStore(syntheticPopulationEnabled: true);
        var builder = new ODataResponseBuilder();
        var query = ODataQueryParser.Parse(new Microsoft.AspNetCore.Http.QueryCollection(new Dictionary<string, Microsoft.Extensions.Primitives.StringValues>
        {
            ["$format"] = "json",
            ["$top"] = "1",
            ["$filter"] = "userId eq 'user.14999'",
            ["$select"] = "userId,jobTitle,company,department,managerId,startDate",
            ["$expand"] = "companyNav,departmentNav"
        }));

        var payload = builder.Build(store.FindByIdentity(query.IdentityField, query.WorkerId), query, "EmpJob");
        using var document = JsonDocument.Parse(JsonSerializer.Serialize(payload));
        var job = document.RootElement.GetProperty("d").GetProperty("results")[0];

        Assert.Equal("user.14999", job.GetProperty("userId").GetString());
        Assert.Equal("Systems Analyst 02-715", job.GetProperty("jobTitle").GetString());
        Assert.Equal("Platform 02-715", job.GetProperty("department").GetString());
        Assert.Equal("CORP", job.GetProperty("company").GetString());
        Assert.False(job.TryGetProperty("personIdExternal", out _));
    }

    [Fact]
    public void EmpJobProjection_SupportsPagedEnumeration_AndHonorsEffectiveDateDefault()
    {
        var store = CreateStore(syntheticPopulationEnabled: true);
        var builder = new ODataResponseBuilder();
        var query = ODataQueryParser.Parse(new Microsoft.AspNetCore.Http.QueryCollection(new Dictionary<string, Microsoft.Extensions.Primitives.StringValues>
        {
            ["$format"] = "json",
            ["$top"] = "3",
            ["$skip"] = "1",
            ["$filter"] = "emplStatus in 'A','U'",
            ["$select"] = "userId,startDate,company,department"
        }));

        var payload = builder.Build(store.QueryWorkers("EmpJob", query), query, "EmpJob");
        using var document = JsonDocument.Parse(JsonSerializer.Serialize(payload));
        var results = document.RootElement.GetProperty("d").GetProperty("results");

        Assert.Equal(3, results.GetArrayLength());
        Assert.Equal("user.10001", results[0].GetProperty("userId").GetString());
        Assert.Equal("user.10003", results[1].GetProperty("userId").GetString());
        Assert.Equal("user.10005", results[2].GetProperty("userId").GetString());
    }

    [Fact]
    public void PerPersonProjection_EmitsNextLink_ForServerSidePagination()
    {
        var store = CreateStore(syntheticPopulationEnabled: true);
        var builder = new ODataResponseBuilder();
        var query = ODataQueryParser.Parse(new Microsoft.AspNetCore.Http.QueryCollection(new Dictionary<string, Microsoft.Extensions.Primitives.StringValues>
        {
            ["$format"] = "json",
            ["customPageSize"] = "2",
            ["paging"] = "snapshot",
            ["$filter"] = "emplStatus in 'A','U'",
            ["$orderby"] = "personIdExternal asc",
            ["$select"] = "personIdExternal"
        }));

        var payload = builder.Build(store.QueryWorkers("PerPerson", query), query, "PerPerson", "http://mock-successfactors.local/odata/v2");
        using var document = JsonDocument.Parse(JsonSerializer.Serialize(payload));
        var d = document.RootElement.GetProperty("d");
        var results = d.GetProperty("results");

        Assert.Equal(2, results.GetArrayLength());
        Assert.Equal("10000", results[0].GetProperty("personIdExternal").GetString());
        Assert.Equal("10001", results[1].GetProperty("personIdExternal").GetString());
        Assert.Equal(
            "http://mock-successfactors.local/odata/v2/PerPerson?$format=json&customPageSize=2&paging=snapshot&$skiptoken=2&$select=personIdExternal&$filter=emplStatus%20in%20%27A%27%2C%27U%27&$orderby=personIdExternal%20asc",
            d.GetProperty("__next").GetString());
    }

    [Fact]
    public void EmpJobProjection_HonorsExplicitAsOfDate()
    {
        var store = CreateStore();
        var builder = new ODataResponseBuilder();
        var query = ODataQueryParser.Parse(new Microsoft.AspNetCore.Http.QueryCollection(new Dictionary<string, Microsoft.Extensions.Primitives.StringValues>
        {
            ["$format"] = "json",
            ["$filter"] = "emplStatus in 'A','U'",
            ["$top"] = "10",
            ["asOfDate"] = "2026-04-03",
            ["$select"] = "userId,startDate"
        }));

        var payload = builder.Build(store.QueryWorkers("EmpJob", query), query, "EmpJob");
        using var document = JsonDocument.Parse(JsonSerializer.Serialize(payload));
        var results = document.RootElement.GetProperty("d").GetProperty("results");

        Assert.Contains(results.EnumerateArray(), row => row.GetProperty("userId").GetString() == "user.10003");
    }

    [Fact]
    public void QueryParser_AcceptsPositiveTopValue()
    {
        var query = ODataQueryParser.Parse(new Microsoft.AspNetCore.Http.QueryCollection(new Dictionary<string, Microsoft.Extensions.Primitives.StringValues>
        {
            ["$format"] = "json",
            ["$top"] = "1",
            ["$filter"] = "userId eq 'user.10001'",
            ["$select"] = "userId"
        }));

        Assert.True(query.IsSupported);
        Assert.Equal("userId", query.IdentityField);
        Assert.Equal("user.10001", query.WorkerId);
    }

    [Fact]
    public void QueryParser_AllowsProbeWithoutFilter()
    {
        var query = ODataQueryParser.Parse(new Microsoft.AspNetCore.Http.QueryCollection(new Dictionary<string, Microsoft.Extensions.Primitives.StringValues>
        {
            ["$format"] = "json",
            ["$top"] = "1",
            ["$select"] = "userId"
        }));

        Assert.True(query.IsSupported);
        Assert.Equal(string.Empty, query.IdentityField);
        Assert.Null(query.WorkerId);
        Assert.Contains("userId", query.Select);
    }

    [Fact]
    public void AuthenticationValidator_AcceptsBearerAndBasicCredentials()
    {
        var bearerRequest = new Microsoft.AspNetCore.Http.DefaultHttpContext().Request;
        bearerRequest.Headers.Authorization = "Bearer mock-access-token";

        var basicRequest = new Microsoft.AspNetCore.Http.DefaultHttpContext().Request;
        basicRequest.Headers.Authorization = $"Basic {Convert.ToBase64String(Encoding.UTF8.GetBytes("mock-user:mock-password"))}";

        var options = new MockAuthenticationOptions();

        Assert.True(AuthenticationValidator.IsAuthorized(bearerRequest, options));
        Assert.True(AuthenticationValidator.IsAuthorized(basicRequest, options));
    }
}

using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using SyncFactors.MockSuccessFactors;

namespace SyncFactors.MockSuccessFactors.Tests;

public sealed class MockApiTests
{
    private static readonly string FixturePath = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "config", "mock-successfactors", "baseline-fixtures.json"));

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
        var store = new MockFixtureStore(Options.Create(new MockSuccessFactorsOptions { FixturePath = FixturePath }));
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
        Assert.Equal("Worker101", worker.GetProperty("personalInfoNav").GetProperty("results")[0].GetProperty("firstName").GetString());
        Assert.Equal("CORP", worker.GetProperty("employmentNav").GetProperty("results")[0].GetProperty("jobInfoNav").GetProperty("results")[0].GetProperty("companyNav").GetProperty("company").GetString());
        Assert.Equal("Central", worker.GetProperty("employmentNav").GetProperty("results")[0].GetProperty("jobInfoNav").GetProperty("results")[0].GetProperty("customString87").GetString());
    }

    [Fact]
    public void PerPersonProjection_ReturnsEmptyResults_WhenWorkerDoesNotExist()
    {
        var store = new MockFixtureStore(Options.Create(new MockSuccessFactorsOptions { FixturePath = FixturePath }));
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
        var store = new MockFixtureStore(Options.Create(new MockSuccessFactorsOptions { FixturePath = FixturePath }));
        var builder = new ODataResponseBuilder();
        var query = ODataQueryParser.Parse(new Microsoft.AspNetCore.Http.QueryCollection(new Dictionary<string, Microsoft.Extensions.Primitives.StringValues>
        {
            ["$format"] = "json",
            ["$top"] = "1",
            ["$filter"] = "userId eq 'user.10001'",
            ["$select"] = "userId,personIdExternal,jobTitle,company,department,division,location,businessUnit,costCenter,employeeClass,employeeType,managerId,customString3,customString20,customString87,customString110,customString111,customString91,startDate",
            ["$expand"] = "companyNav,departmentNav,divisionNav,locationNav,businessUnitNav,costCenterNav"
        }));

        var payload = builder.Build(store.FindByIdentity(query.IdentityField, query.WorkerId), query, "EmpJob");
        using var document = JsonDocument.Parse(JsonSerializer.Serialize(payload));
        var job = document.RootElement.GetProperty("d").GetProperty("results")[0];

        Assert.Equal("user.10001", job.GetProperty("userId").GetString());
        Assert.Equal("10001", job.GetProperty("personIdExternal").GetString());
        Assert.Equal("CORP", job.GetProperty("company").GetString());
        Assert.Equal("CORP", job.GetProperty("companyNav").GetProperty("company").GetString());
        Assert.Equal("Central", job.GetProperty("customString87").GetString());
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

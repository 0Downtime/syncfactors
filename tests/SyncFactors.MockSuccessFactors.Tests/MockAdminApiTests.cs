using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

namespace SyncFactors.MockSuccessFactors.Tests;

public sealed class MockAdminApiTests
{
    private static readonly string FixturePath = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "config", "mock-successfactors", "baseline-fixtures.json"));

    [Fact]
    public async Task AdminApi_RejectsNonLoopbackHosts()
    {
        await using var factory = new MockSuccessFactorsFactory(CreateRuntimePath());
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            BaseAddress = new Uri("http://example.test")
        });

        var response = await client.GetAsync("/api/admin/workers");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task AdminApi_RejectsDuplicateIdentity()
    {
        await using var factory = new MockSuccessFactorsFactory(CreateRuntimePath());
        using var client = CreateLoopbackClient(factory);

        var response = await client.PostAsJsonAsync("/api/admin/workers", new
        {
            personIdExternal = "10001",
            firstName = "Ada",
            lastName = "Lovelace",
            startDate = "2026-04-22"
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var payload = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Contains("already in use", payload.GetProperty("error").GetString(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task AdminApi_RejectsUnknownManager()
    {
        await using var factory = new MockSuccessFactorsFactory(CreateRuntimePath());
        using var client = CreateLoopbackClient(factory);

        var response = await client.PostAsJsonAsync("/api/admin/workers", new
        {
            firstName = "Grace",
            lastName = "Hopper",
            startDate = "2026-04-22",
            managerId = "99999"
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var payload = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Contains("managerId", payload.GetProperty("error").GetString(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task AdminApi_Mutations_AreVisibleThroughODataImmediately()
    {
        await using var factory = new MockSuccessFactorsFactory(CreateRuntimePath());
        using var adminClient = CreateLoopbackClient(factory);
        using var odataClient = CreateLoopbackClient(factory);
        odataClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "mock-access-token");

        var createResponse = await adminClient.PostAsJsonAsync("/api/admin/workers", new
        {
            firstName = "Terry",
            lastName = "Pratchett",
            startDate = "2026-04-22",
            company = "CORP",
            department = "Writers"
        });
        createResponse.EnsureSuccessStatusCode();
        var createdPayload = await createResponse.Content.ReadFromJsonAsync<JsonElement>();
        var workerId = createdPayload.GetProperty("worker").GetProperty("personIdExternal").GetString();
        var userId = createdPayload.GetProperty("worker").GetProperty("userId").GetString();

        var perPersonResponse = await odataClient.GetAsync($"/odata/v2/PerPerson?$format=json&$filter=personIdExternal%20eq%20'{workerId}'&$select=personIdExternal,personalInfoNav/firstName,employmentNav/userId,emailNav/emailAddress&$expand=employmentNav,personalInfoNav,emailNav");
        perPersonResponse.EnsureSuccessStatusCode();
        var perPersonJson = JsonDocument.Parse(await perPersonResponse.Content.ReadAsStringAsync());
        Assert.Equal(workerId, perPersonJson.RootElement.GetProperty("d").GetProperty("results")[0].GetProperty("personIdExternal").GetString());

        var terminateResponse = await adminClient.PostAsync($"/api/admin/workers/{workerId}/terminate", JsonContent.Create(new { }));
        terminateResponse.EnsureSuccessStatusCode();

        var empJobResponse = await odataClient.GetAsync($"/odata/v2/EmpJob?$format=json&$filter=userId%20eq%20'{userId}'&$select=userId,emplStatus,endDate");
        empJobResponse.EnsureSuccessStatusCode();
        var empJobJson = JsonDocument.Parse(await empJobResponse.Content.ReadAsStringAsync());
        var job = empJobJson.RootElement.GetProperty("d").GetProperty("results")[0];
        Assert.Equal("T", job.GetProperty("emplStatus").GetString());
        Assert.False(string.IsNullOrWhiteSpace(job.GetProperty("endDate").GetString()));

        var deleteResponse = await adminClient.DeleteAsync($"/api/admin/workers/{workerId}");
        deleteResponse.EnsureSuccessStatusCode();

        var deletedResponse = await odataClient.GetAsync($"/odata/v2/PerPerson?$format=json&$filter=personIdExternal%20eq%20'{workerId}'&$select=personIdExternal");
        deletedResponse.EnsureSuccessStatusCode();
        var deletedJson = JsonDocument.Parse(await deletedResponse.Content.ReadAsStringAsync());
        Assert.Equal(0, deletedJson.RootElement.GetProperty("d").GetProperty("results").GetArrayLength());
    }

    [Theory]
    [InlineData("prehire", "A", false)]
    [InlineData("active-started", "A", false)]
    [InlineData("paid-leave", "U", false)]
    [InlineData("unpaid-leave", "64303", false)]
    [InlineData("returned-from-leave", "A", false)]
    [InlineData("terminated", "T", true)]
    public async Task AdminApi_LifecycleStateMutation_IsVisibleThroughODataImmediately(
        string lifecycleState,
        string expectedStatus,
        bool expectEndDate)
    {
        await using var factory = new MockSuccessFactorsFactory(CreateRuntimePath());
        using var adminClient = CreateLoopbackClient(factory);
        using var odataClient = CreateLoopbackClient(factory);
        odataClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "mock-access-token");

        var createResponse = await adminClient.PostAsJsonAsync("/api/admin/workers", new
        {
            firstName = "Lifecycle",
            lastName = "Tester",
            startDate = DateTimeOffset.UtcNow.Date.ToString("yyyy-MM-dd"),
            company = "CORP",
            department = "QA"
        });
        createResponse.EnsureSuccessStatusCode();
        var createdPayload = await createResponse.Content.ReadFromJsonAsync<JsonElement>();
        var workerId = createdPayload.GetProperty("worker").GetProperty("personIdExternal").GetString();
        var userId = createdPayload.GetProperty("worker").GetProperty("userId").GetString();

        var lifecycleResponse = await adminClient.PostAsJsonAsync(
            $"/api/admin/workers/{workerId}/lifecycle-state",
            new { lifecycleState });
        lifecycleResponse.EnsureSuccessStatusCode();

        var empJobResponse = await odataClient.GetAsync($"/odata/v2/EmpJob?$format=json&$filter=userId%20eq%20'{userId}'&$select=userId,emplStatus,startDate,endDate");
        empJobResponse.EnsureSuccessStatusCode();
        var empJobJson = JsonDocument.Parse(await empJobResponse.Content.ReadAsStringAsync());
        var job = empJobJson.RootElement.GetProperty("d").GetProperty("results")[0];

        Assert.Equal(expectedStatus, job.GetProperty("emplStatus").GetString());
        Assert.Equal(expectEndDate, job.TryGetProperty("endDate", out var endDate) && !string.IsNullOrWhiteSpace(endDate.GetString()));
    }

    private static HttpClient CreateLoopbackClient(WebApplicationFactory<Program> factory)
    {
        return factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            BaseAddress = new Uri("http://localhost")
        });
    }

    private static string CreateRuntimePath()
        => Path.Combine(Path.GetTempPath(), $"mock-successfactors-api-{Guid.NewGuid():N}.json");

    private sealed class MockSuccessFactorsFactory(string runtimePath) : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Development");
            builder.ConfigureAppConfiguration((_, configBuilder) =>
            {
                configBuilder.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["MockSuccessFactors:FixturePath"] = FixturePath,
                    ["MockSuccessFactors:Runtime:FixturePath"] = runtimePath,
                    ["MockSuccessFactors:Admin:Enabled"] = "true",
                    ["MockSuccessFactors:Admin:RequireLoopback"] = "true",
                    ["MockSuccessFactors:Admin:Path"] = "/admin",
                    ["MockSuccessFactors:SyntheticPopulation:Enabled"] = "false"
                });
            });
        }
    }
}

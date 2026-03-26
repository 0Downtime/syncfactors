using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using SyncFactors.Infrastructure;
using SyncFactors.MockSuccessFactors;

namespace SyncFactors.MockSuccessFactors.Tests;

public sealed class SuccessFactorsWorkerSourceIntegrationTests
{
    [Fact]
    public async Task WorkerSource_CanResolveWorker_FromMockApi()
    {
        var fixturePath = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "config", "mock-successfactors", "baseline-fixtures.json"));
        var fixtureStore = new MockFixtureStore(Options.Create(new MockSuccessFactorsOptions
        {
            FixturePath = fixturePath
        }));
        var responseBuilder = new ODataResponseBuilder();
        using var client = new HttpClient(new MockSuccessFactorsHttpHandler(fixtureStore, responseBuilder))
        {
            BaseAddress = new Uri("http://mock-successfactors.local")
        };

        var tempDirectory = Path.Combine(Path.GetTempPath(), "syncfactors-worker-source", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectory);

        var syncConfigPath = Path.Combine(tempDirectory, "sync-config.json");
        var mappingConfigPath = Path.Combine(tempDirectory, "mapping-config.json");
        var scaffoldDataPath = Path.Combine(tempDirectory, "scaffold-data.json");

        await File.WriteAllTextAsync(syncConfigPath, """
        {
          "secrets": {
            "adServerEnv": null,
            "adUsernameEnv": null,
            "adBindPasswordEnv": null,
            "successFactorsClientIdEnv": null,
            "successFactorsClientSecretEnv": null
          },
          "successFactors": {
            "baseUrl": "http://mock-successfactors.local/odata/v2",
            "auth": {
              "mode": "oauth",
              "oauth": {
                "tokenUrl": "http://mock-successfactors.local/oauth/token",
                "clientId": "mock-client-id",
                "clientSecret": "mock-client-secret",
                "companyId": "MOCK"
              }
            },
            "query": {
              "entitySet": "PerPerson",
              "identityField": "personIdExternal",
              "deltaField": "lastModifiedDateTime",
              "select": [
                "personIdExternal",
                "personalInfoNav/firstName",
                "personalInfoNav/lastName",
                "employmentNav/startDate",
                "emailNav/emailAddress",
                "employmentNav/jobInfoNav/departmentNav/department",
                "employmentNav/jobInfoNav/companyNav/company",
                "employmentNav/jobInfoNav/locationNav/LocationName",
                "employmentNav/jobInfoNav/jobTitle",
                "employmentNav/jobInfoNav/businessUnitNav/businessUnit",
                "employmentNav/jobInfoNav/divisionNav/division",
                "employmentNav/jobInfoNav/costCenterNav/costCenterDescription",
                "employmentNav/jobInfoNav/employeeClass",
                "employmentNav/jobInfoNav/employeeType",
                "employmentNav/jobInfoNav/managerId",
                "employmentNav/jobInfoNav/customString3",
                "employmentNav/jobInfoNav/customString20",
                "employmentNav/jobInfoNav/customString87",
                "employmentNav/jobInfoNav/customString110",
                "employmentNav/jobInfoNav/customString111",
                "employmentNav/jobInfoNav/customString91"
              ],
              "expand": [
                "employmentNav",
                "employmentNav/jobInfoNav",
                "personalInfoNav",
                "emailNav",
                "employmentNav/jobInfoNav/companyNav",
                "employmentNav/jobInfoNav/departmentNav",
                "employmentNav/jobInfoNav/businessUnitNav",
                "employmentNav/jobInfoNav/costCenterNav",
                "employmentNav/jobInfoNav/divisionNav",
                "employmentNav/jobInfoNav/locationNav"
              ]
            }
          },
          "ad": {
            "server": "ldap.example.test",
            "username": "",
            "bindPassword": "",
            "identityAttribute": "employeeID",
            "defaultActiveOu": "OU=LabUsers,DC=example,DC=com",
            "graveyardOu": "OU=LabGraveyard,DC=example,DC=com"
          },
          "sync": {
            "enableBeforeStartDays": 7,
            "deletionRetentionDays": 90
          },
          "safety": {
            "maxCreatesPerRun": 10,
            "maxDisablesPerRun": 10,
            "maxDeletionsPerRun": 10
          },
          "reporting": {
            "outputDirectory": "reports"
          }
        }
        """);
        File.Copy(Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "config", "sample.syncfactors.mapping-config.json")), mappingConfigPath);
        await File.WriteAllTextAsync(scaffoldDataPath, """{"workers":[],"directoryUsers":[]}""");

        var configLoader = new SyncFactorsConfigurationLoader(new SyncFactorsConfigPathResolver(syncConfigPath, mappingConfigPath));
        var scaffoldStore = new ScaffoldDataStore(new ScaffoldDataPathResolver(scaffoldDataPath));
        var fallbackSource = new ScaffoldWorkerSource(scaffoldStore);
        var workerSource = new SuccessFactorsWorkerSource(client, configLoader, fallbackSource, NullLogger<SuccessFactorsWorkerSource>.Instance);

        var worker = await workerSource.GetWorkerAsync("mock-10001", CancellationToken.None);

        Assert.NotNull(worker);
        Assert.Equal("mock-10001", worker!.WorkerId);
        Assert.Equal("Worker101", worker.PreferredName);
        Assert.Equal("CORP", worker.Attributes["company"]);
        Assert.Equal("HQ North", worker.Attributes["location"]);
        Assert.Equal("Field Ops", worker.Attributes["peopleGroup"]);
        Assert.Equal("Central", worker.Attributes["region"]);
        Assert.Equal("North Metro", worker.Attributes["geozone"]);
        Assert.Equal("IC-3", worker.Attributes["leadershipLevel"]);
    }

    private sealed class MockSuccessFactorsHttpHandler(MockFixtureStore fixtureStore, ODataResponseBuilder responseBuilder) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            _ = cancellationToken;

            if (request.RequestUri is null)
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.BadRequest));
            }

            if (request.RequestUri.AbsolutePath.Equals("/oauth/token", StringComparison.OrdinalIgnoreCase))
            {
                var tokenJson = JsonSerializer.Serialize(new TokenResponse("mock-access-token", "Bearer", 3600));
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(tokenJson, Encoding.UTF8, "application/json")
                });
            }

            if (request.RequestUri.AbsolutePath.Equals("/odata/v2/PerPerson", StringComparison.OrdinalIgnoreCase))
            {
                if (request.Headers.Authorization is not AuthenticationHeaderValue auth ||
                    !string.Equals(auth.Scheme, "Bearer", StringComparison.OrdinalIgnoreCase) ||
                    !string.Equals(auth.Parameter, "mock-access-token", StringComparison.Ordinal))
                {
                    return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Unauthorized));
                }

                var queryCollection = Microsoft.AspNetCore.WebUtilities.QueryHelpers.ParseQuery(request.RequestUri.Query);
                var query = ODataQueryParser.Parse(new Microsoft.AspNetCore.Http.QueryCollection(queryCollection));
                var worker = fixtureStore.FindByIdentity(query.IdentityField, query.WorkerId);
                var payload = responseBuilder.Build(worker, query);
                var json = JsonSerializer.Serialize(payload);

                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(json, Encoding.UTF8, "application/json")
                });
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        }
    }
}

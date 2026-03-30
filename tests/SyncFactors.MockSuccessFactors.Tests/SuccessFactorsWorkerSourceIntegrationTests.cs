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
        File.Copy(Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "config", "sample.empjob-confirmed.mapping-config.json")), mappingConfigPath);
        await File.WriteAllTextAsync(scaffoldDataPath, """{"workers":[],"directoryUsers":[]}""");

        var configLoader = new SyncFactorsConfigurationLoader(new SyncFactorsConfigPathResolver(syncConfigPath, mappingConfigPath));
        var scaffoldStore = new ScaffoldDataStore(new ScaffoldDataPathResolver(scaffoldDataPath));
        var fallbackSource = new ScaffoldWorkerSource(scaffoldStore);
        var workerSource = new SuccessFactorsWorkerSource(client, configLoader, fallbackSource, NullLogger<SuccessFactorsWorkerSource>.Instance);

        var worker = await workerSource.GetWorkerAsync("10001", CancellationToken.None);

        Assert.NotNull(worker);
        Assert.Equal("10001", worker!.WorkerId);
        Assert.Equal("Worker101", worker.PreferredName);
        Assert.Equal("CORP", worker.Attributes["company"]);
        Assert.Equal("HQ North", worker.Attributes["location"]);
        Assert.Equal("Field Ops", worker.Attributes["peopleGroup"]);
        Assert.Equal("Central", worker.Attributes["region"]);
        Assert.Equal("North Metro", worker.Attributes["geozone"]);
        Assert.Equal("IC-3", worker.Attributes["leadershipLevel"]);
    }

    [Fact]
    public async Task WorkerSource_CanResolveWorker_FromMockApi_WithExportStyleQuery()
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
            "adBindPasswordEnv": null
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
                "emailNav/emailAddress",
                "emailNav/isPrimary",
                "employmentNav/startDate",
                "employmentNav/jobInfoNav/jobTitle",
                "employmentNav/jobInfoNav/companyNav/name_localized",
                "employmentNav/jobInfoNav/departmentNav/name_localized",
                "employmentNav/jobInfoNav/divisionNav/name_localized",
                "employmentNav/jobInfoNav/businessUnitNav/name_localized",
                "employmentNav/jobInfoNav/locationNav/name",
                "employmentNav/jobInfoNav/locationNav/addressNavDEFLT/address1",
                "employmentNav/jobInfoNav/locationNav/addressNavDEFLT/city",
                "employmentNav/jobInfoNav/locationNav/addressNavDEFLT/zipCode",
                "employmentNav/userNav/manager/empInfo/personIdExternal",
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
                "employmentNav/jobInfoNav/divisionNav",
                "employmentNav/jobInfoNav/businessUnitNav",
                "employmentNav/jobInfoNav/locationNav",
                "employmentNav/jobInfoNav/locationNav/addressNavDEFLT",
                "employmentNav/userNav",
                "employmentNav/userNav/manager",
                "employmentNav/userNav/manager/empInfo"
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
        File.Copy(Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "config", "sample.empjob-confirmed.mapping-config.json")), mappingConfigPath);
        await File.WriteAllTextAsync(scaffoldDataPath, """{"workers":[],"directoryUsers":[]}""");

        var configLoader = new SyncFactorsConfigurationLoader(new SyncFactorsConfigPathResolver(syncConfigPath, mappingConfigPath));
        var scaffoldStore = new ScaffoldDataStore(new ScaffoldDataPathResolver(scaffoldDataPath));
        var fallbackSource = new ScaffoldWorkerSource(scaffoldStore);
        var workerSource = new SuccessFactorsWorkerSource(client, configLoader, fallbackSource, NullLogger<SuccessFactorsWorkerSource>.Instance);

        var worker = await workerSource.GetWorkerAsync("10001", CancellationToken.None);

        Assert.NotNull(worker);
        Assert.Equal("10001", worker!.WorkerId);
        Assert.Equal("CORP", worker.Attributes["company"]);
        Assert.Equal("IT", worker.Attributes["department"]);
        Assert.Equal("Operations", worker.Attributes["division"]);
        Assert.Equal("Infrastructure", worker.Attributes["businessUnit"]);
        Assert.Equal("HQ North", worker.Attributes["location"]);
        Assert.Equal("101 Example Way", worker.Attributes["officeLocationAddress"]);
        Assert.Equal("Exampletown", worker.Attributes["officeLocationCity"]);
        Assert.Equal("10001", worker.Attributes["officeLocationZipCode"]);
        Assert.Equal("90001", worker.Attributes["managerId"]);
        Assert.Equal("Field Ops", worker.Attributes["peopleGroup"]);
    }

    [Fact]
    public async Task WorkerSource_CanResolveWorker_FromExportStylePayload()
    {
        using var client = new HttpClient(new ExportStyleSuccessFactorsHttpHandler())
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
            "adBindPasswordEnv": null
          },
          "successFactors": {
            "baseUrl": "http://mock-successfactors.local/odata/v2",
            "auth": {
              "mode": "basic",
              "basic": {
                "username": "mock-user",
                "password": "mock-password"
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
                "personalInfoNav/preferredName",
                "personalInfoNav/displayName",
                "emailNav/emailAddress",
                "emailNav/isPrimary",
                "emailNav/emailType",
                "employmentNav/startDate",
                "employmentNav/userId",
                "employmentNav/jobInfoNav/jobTitle",
                "employmentNav/jobInfoNav/customString3",
                "employmentNav/jobInfoNav/customString20",
                "employmentNav/jobInfoNav/customString87",
                "employmentNav/jobInfoNav/customString110",
                "employmentNav/jobInfoNav/customString111",
                "employmentNav/jobInfoNav/customString91",
                "employmentNav/jobInfoNav/companyNav/name_localized",
                "employmentNav/jobInfoNav/companyNav/externalCode",
                "employmentNav/jobInfoNav/departmentNav/name_localized",
                "employmentNav/jobInfoNav/divisionNav/name_localized",
                "employmentNav/jobInfoNav/businessUnitNav/name_localized",
                "employmentNav/jobInfoNav/businessUnitNav/externalCode",
                "employmentNav/jobInfoNav/costCenterNav/name_localized",
                "employmentNav/jobInfoNav/costCenterNav/description_localized",
                "employmentNav/jobInfoNav/costCenterNav/externalCode",
                "employmentNav/jobInfoNav/locationNav/name",
                "employmentNav/jobInfoNav/locationNav/addressNavDEFLT/address1",
                "employmentNav/jobInfoNav/locationNav/addressNavDEFLT/city",
                "employmentNav/jobInfoNav/locationNav/addressNavDEFLT/zipCode",
                "employmentNav/jobInfoNav/locationNav/addressNavDEFLT/customString4",
                "employmentNav/jobInfoNav/companyNav/countryOfRegistrationNav/twoCharCountryCode",
                "employmentNav/userNav/manager/empInfo/personIdExternal",
                "phoneNav/areaCode",
                "phoneNav/countryCode",
                "phoneNav/extension",
                "phoneNav/phoneNumber",
                "phoneNav/phoneType",
                "phoneNav/isPrimary"
              ],
              "expand": [
                "personalInfoNav",
                "emailNav",
                "employmentNav",
                "employmentNav/jobInfoNav",
                "employmentNav/jobInfoNav/companyNav",
                "employmentNav/jobInfoNav/companyNav/countryOfRegistrationNav",
                "employmentNav/jobInfoNav/departmentNav",
                "employmentNav/jobInfoNav/divisionNav",
                "employmentNav/jobInfoNav/businessUnitNav",
                "employmentNav/jobInfoNav/costCenterNav",
                "employmentNav/jobInfoNav/locationNav",
                "employmentNav/jobInfoNav/locationNav/addressNavDEFLT",
                "employmentNav/userNav",
                "employmentNav/userNav/manager",
                "employmentNav/userNav/manager/empInfo",
                "phoneNav"
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
        File.Copy(Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "config", "sample.empjob-confirmed.mapping-config.json")), mappingConfigPath);
        await File.WriteAllTextAsync(scaffoldDataPath, """{"workers":[],"directoryUsers":[]}""");

        var configLoader = new SyncFactorsConfigurationLoader(new SyncFactorsConfigPathResolver(syncConfigPath, mappingConfigPath));
        var scaffoldStore = new ScaffoldDataStore(new ScaffoldDataPathResolver(scaffoldDataPath));
        var fallbackSource = new ScaffoldWorkerSource(scaffoldStore);
        var workerSource = new SuccessFactorsWorkerSource(client, configLoader, fallbackSource, NullLogger<SuccessFactorsWorkerSource>.Instance);

        var worker = await workerSource.GetWorkerAsync("10001", CancellationToken.None);

        Assert.NotNull(worker);
        Assert.Equal("10001", worker!.WorkerId);
        Assert.Equal("Winnie", worker.PreferredName);
        Assert.Equal("Sample101", worker.LastName);
        Assert.Equal("IT", worker.Department);
        Assert.Equal("user.10001.primary@example.test", worker.Attributes["email"]);
        Assert.Equal("CORP", worker.Attributes["company"]);
        Assert.Equal("IT", worker.Attributes["department"]);
        Assert.Equal("Infrastructure", worker.Attributes["businessUnit"]);
        Assert.Equal("BU-001", worker.Attributes["businessUnitId"]);
        Assert.Equal("CC-100", worker.Attributes["costCenter"]);
        Assert.Equal("Engineering", worker.Attributes["costCenterDescription"]);
        Assert.Equal("CC100", worker.Attributes["costCenterId"]);
        Assert.Equal("HQ North", worker.Attributes["location"]);
        Assert.Equal("101 Example Way", worker.Attributes["officeLocationAddress"]);
        Assert.Equal("Exampletown", worker.Attributes["officeLocationCity"]);
        Assert.Equal("10001", worker.Attributes["officeLocationZipCode"]);
        Assert.Equal("Floor 1", worker.Attributes["officeLocationCustomString4"]);
        Assert.Equal("90001", worker.Attributes["managerId"]);
        Assert.Equal("1", worker.Attributes["activeEmploymentsCount"]);
        Assert.Equal("212", worker.Attributes["businessPhoneAreaCode"]);
        Assert.Equal("5550101", worker.Attributes["businessPhoneNumber"]);
        Assert.Equal("5550102", worker.Attributes["cellPhoneNumber"]);
        Assert.Equal("user.10001.primary@example.test", worker.Attributes["emailNav[?(@.isPrimary == true)].emailAddress"]);
        Assert.Equal("CORP", worker.Attributes["employmentNav[0].jobInfoNav[0].companyNav.name_localized"]);
        Assert.Equal("IT", worker.Attributes["employmentNav[0].jobInfoNav[0].departmentNav.name_localized"]);
        Assert.Equal("HQ North", worker.Attributes["employmentNav[0].jobInfoNav[0].locationNav.name"]);
        Assert.Equal("101 Example Way", worker.Attributes["employmentNav[0].jobInfoNav[0].locationNav.addressNavDEFLT.address1"]);
    }

    [Fact]
    public async Task WorkerSource_IncludesResponseBody_WhenSuccessFactorsReturnsBadRequest()
    {
        using var client = new HttpClient(new ErroringSuccessFactorsHttpHandler())
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
            "adBindPasswordEnv": null
          },
          "successFactors": {
            "baseUrl": "http://mock-successfactors.local/odata/v2",
            "auth": {
              "mode": "basic",
              "basic": {
                "username": "mock-user",
                "password": "mock-password"
              }
            },
            "query": {
              "entitySet": "PerPerson",
              "identityField": "personIdExternal",
              "deltaField": "lastModifiedDateTime",
              "select": [
                "personIdExternal",
                "personalInfoNav/firstName"
              ],
              "expand": [
                "personalInfoNav"
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
        File.Copy(Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "config", "sample.empjob-confirmed.mapping-config.json")), mappingConfigPath);
        await File.WriteAllTextAsync(scaffoldDataPath, """{"workers":[],"directoryUsers":[]}""");

        var configLoader = new SyncFactorsConfigurationLoader(new SyncFactorsConfigPathResolver(syncConfigPath, mappingConfigPath));
        var scaffoldStore = new ScaffoldDataStore(new ScaffoldDataPathResolver(scaffoldDataPath));
        var fallbackSource = new ScaffoldWorkerSource(scaffoldStore);
        var workerSource = new SuccessFactorsWorkerSource(client, configLoader, fallbackSource, NullLogger<SuccessFactorsWorkerSource>.Instance);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => workerSource.GetWorkerAsync("10001", CancellationToken.None));
        Assert.Contains("SuccessFactors request failed.", ex.Message);
        Assert.Contains("Status=400", ex.Message);
        Assert.Contains("COE_PROPERTY_NOT_FOUND", ex.Message);
        Assert.Contains("RequestUri=http://mock-successfactors.local/odata/v2/PerPerson", ex.Message);
    }

    [Fact]
    public async Task WorkerSource_RetriesWithoutInvalidConfiguredProperty_WhenSuccessFactorsRejectsSelectPath()
    {
        using var client = new HttpClient(new RetryOnInvalidPropertyHttpHandler())
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
            "adBindPasswordEnv": null
          },
          "successFactors": {
            "baseUrl": "http://mock-successfactors.local/odata/v2",
            "auth": {
              "mode": "basic",
              "basic": {
                "username": "mock-user",
                "password": "mock-password"
              }
            },
            "query": {
              "entitySet": "PerPerson",
              "identityField": "personIdExternal",
              "deltaField": "lastModifiedDateTime",
              "select": [
                "personIdExternal",
                "personalInfoNav/firstName",
                "employmentNav/jobInfoNav/departmentNav/department",
                "employmentNav/jobInfoNav/customString91"
              ],
              "expand": [
                "personalInfoNav",
                "employmentNav",
                "employmentNav/jobInfoNav",
                "employmentNav/jobInfoNav/departmentNav"
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
        File.Copy(Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "config", "sample.empjob-confirmed.mapping-config.json")), mappingConfigPath);
        await File.WriteAllTextAsync(scaffoldDataPath, """{"workers":[],"directoryUsers":[]}""");

        var configLoader = new SyncFactorsConfigurationLoader(new SyncFactorsConfigPathResolver(syncConfigPath, mappingConfigPath));
        var scaffoldStore = new ScaffoldDataStore(new ScaffoldDataPathResolver(scaffoldDataPath));
        var fallbackSource = new ScaffoldWorkerSource(scaffoldStore);
        var workerSource = new SuccessFactorsWorkerSource(client, configLoader, fallbackSource, NullLogger<SuccessFactorsWorkerSource>.Instance);

        var worker = await workerSource.GetWorkerAsync("10001", CancellationToken.None);

        Assert.NotNull(worker);
        Assert.Equal("10001", worker!.WorkerId);
        Assert.Equal("Union-17", worker.Attributes["unionJobCode"]);
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

    private sealed class ExportStyleSuccessFactorsHttpHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            _ = cancellationToken;

            var successJson = """
            {
              "d": {
                "results": [
                  {
                    "personIdExternal": "10001",
                    "personId": "10001",
                    "perPersonUuid": "uuid-10001",
                    "personEmpTerminationInfoNav": {
                      "activeEmploymentsCount": 1,
                      "latestTerminationDate": "2025-12-31T00:00:00Z"
                    },
                    "personalInfoNav": {
                      "results": [
                        {
                          "firstName": "Worker101",
                          "lastName": "Sample101",
                          "preferredName": "Winnie",
                          "displayName": "Winnie Sample101"
                        }
                      ]
                    },
                    "emailNav": {
                      "results": [
                        {
                          "emailAddress": "user.10001.secondary@example.test",
                          "emailType": "P",
                          "isPrimary": false
                        },
                        {
                          "emailAddress": "user.10001.primary@example.test",
                          "emailType": "B",
                          "isPrimary": true
                        }
                      ]
                    },
                    "phoneNav": {
                      "results": [
                        {
                          "areaCode": "212",
                          "countryCode": "1",
                          "extension": "101",
                          "phoneNumber": "5550101",
                          "phoneType": "10605",
                          "isPrimary": false
                        },
                        {
                          "areaCode": "917",
                          "countryCode": "1",
                          "phoneNumber": "5550102",
                          "phoneType": "10606",
                          "isPrimary": true
                        }
                      ]
                    },
                    "employmentNav": {
                      "results": [
                        {
                          "userId": "user.10001",
                          "startDate": "2026-03-10T00:00:00Z",
                          "endDate": "9999-12-31T00:00:00Z",
                          "firstDateWorked": "2026-03-10T00:00:00Z",
                          "lastDateWorked": null,
                          "isContingentWorker": false,
                          "customString1": "EmpNavOne",
                          "userNav": {
                            "username": "user.10001",
                            "addressLine1": "101 Example Way",
                            "city": "Exampletown",
                            "state": "NY",
                            "zipCode": "10001",
                            "country": "US",
                            "businessPhone": "2125550101",
                            "cellPhone": "9175550102",
                            "custom01": "UserCustom01",
                            "manager": {
                              "empInfo": {
                                "personIdExternal": "90001"
                              }
                            }
                          },
                          "jobInfoNav": {
                            "results": [
                              {
                                "jobTitle": "Platform Engineer",
                                "employeeType": "Regular",
                                "emplStatus": "A",
                                "position": "POS-100",
                                "customString3": "Field Ops",
                                "customString20": "IC-3",
                                "customString87": "Central",
                                "customString110": "North Metro",
                                "customString111": "Non-Union",
                                "customString91": "UJ-100",
                                "customString1": "JobCustom01",
                                "companyNav": {
                                  "name_localized": "CORP",
                                  "externalCode": "COMP-001",
                                  "countryOfRegistrationNav": {
                                    "twoCharCountryCode": "US"
                                  }
                                },
                                "departmentNav": {
                                  "name_localized": "IT",
                                  "name": "Information Technology",
                                  "externalCode": "DEPT-001",
                                  "costCenter": "CC-100"
                                },
                                "divisionNav": {
                                  "name_localized": "Operations",
                                  "externalCode": "DIV-001"
                                },
                                "businessUnitNav": {
                                  "name_localized": "Infrastructure",
                                  "externalCode": "BU-001"
                                },
                                "costCenterNav": {
                                  "name_localized": "CC-100",
                                  "description_localized": "Engineering",
                                  "externalCode": "CC100"
                                },
                                "locationNav": {
                                  "name": "HQ North",
                                  "addressNavDEFLT": {
                                    "address1": "101 Example Way",
                                    "city": "Exampletown",
                                    "zipCode": "10001",
                                    "customString4": "Floor 1"
                                  }
                                }
                              }
                            ]
                          }
                        }
                      ]
                    }
                  }
                ]
              }
            }
            """;

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(successJson, Encoding.UTF8, "application/json")
            });
        }
    }

    private sealed class ErroringSuccessFactorsHttpHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            _ = cancellationToken;

            var errorJson = """
            {
              "error": {
                "code": "COE_PROPERTY_NOT_FOUND",
                "message": {
                  "lang": "en-US",
                  "value": "[COE0021]Invalid property names: FOBusinessUnit/businessUnit."
                }
              }
            }
            """;

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.BadRequest)
            {
                Content = new StringContent(errorJson, Encoding.UTF8, "application/json")
            });
        }
    }

    private sealed class RetryOnInvalidPropertyHttpHandler : HttpMessageHandler
    {
        private int _requestCount;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            _ = cancellationToken;
            _requestCount++;

            var queryCollection = Microsoft.AspNetCore.WebUtilities.QueryHelpers.ParseQuery(request.RequestUri!.Query);
            var select = queryCollection["$select"].ToString();
            if (_requestCount == 1 && select.Contains("employmentNav/jobInfoNav/departmentNav/department", StringComparison.Ordinal))
            {
                var errorJson = """
                {
                  "error": {
                    "code": "COE_PROPERTY_NOT_FOUND",
                    "message": {
                      "lang": "en-US",
                      "value": "[COE0021]Invalid property names: FODepartment/department."
                    }
                  }
                }
                """;

                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.BadRequest)
                {
                    Content = new StringContent(errorJson, Encoding.UTF8, "application/json")
                });
            }

            var successJson = """
            {
              "d": {
                "results": [
                  {
                    "personIdExternal": "10001",
                    "personalInfoNav": {
                      "results": [
                        {
                          "firstName": "Worker101"
                        }
                      ]
                    },
                    "employmentNav": {
                      "results": [
                        {
                          "jobInfoNav": {
                            "results": [
                              {
                                "customString91": "Union-17"
                              }
                            ]
                          }
                        }
                      ]
                    }
                  }
                ]
              }
            }
            """;

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(successJson, Encoding.UTF8, "application/json")
            });
        }
    }
}

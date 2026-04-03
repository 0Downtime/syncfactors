using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;

namespace SyncFactors.Api.Tests;

public sealed class HealthEndpointsTests
{
    [Fact]
    public async Task ApiHealth_RejectsAnonymousAccess()
    {
        await using var factory = new SyncFactorsApiFactory();
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
            BaseAddress = new Uri("https://localhost")
        });

        using var response = await client.GetAsync("/api/health");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Healthz_AllowsAnonymousAccess()
    {
        await using var factory = new SyncFactorsApiFactory();
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
            BaseAddress = new Uri("https://localhost")
        });

        using var response = await client.GetAsync("/healthz");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var payload = await response.Content.ReadFromJsonAsync<HealthzResponse>();
        Assert.NotNull(payload);
        Assert.Equal("ok", payload.Status);
    }

    private sealed class SyncFactorsApiFactory : WebApplicationFactory<Program>
    {
        private readonly string _tempRoot = Path.Combine(Path.GetTempPath(), "syncfactors-api-tests", Guid.NewGuid().ToString("N"));
        private readonly string? _originalConfigPath = Environment.GetEnvironmentVariable("SYNCFACTORS_CONFIG_PATH");
        private readonly string? _originalMappingConfigPath = Environment.GetEnvironmentVariable("SYNCFACTORS_MAPPING_CONFIG_PATH");
        private readonly string? _originalAspNetCoreEnvironment = Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT");
        private readonly string? _originalDotNetEnvironment = Environment.GetEnvironmentVariable("DOTNET_ENVIRONMENT");

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            Directory.CreateDirectory(_tempRoot);
            var sqlitePath = Path.Combine(_tempRoot, "runtime.db");
            var syncConfigPath = Path.Combine(_tempRoot, "sync-config.json");
            var mappingConfigPath = Path.Combine(_tempRoot, "mapping-config.json");
            var reportingPath = Path.Combine(_tempRoot, "reports");

            Directory.CreateDirectory(reportingPath);

            File.WriteAllText(syncConfigPath, $$"""
            {
              "secrets": {},
              "ad": {
                "server": "ldap.example.invalid:636",
                "username": "",
                "bindPassword": "",
                "defaultActiveOu": "OU=LabUsers,DC=example,DC=com",
                "prehireOu": "OU=Prehire,DC=example,DC=com",
                "graveyardOu": "OU=Graveyard,DC=example,DC=com",
                "identityAttribute": "employeeID"
              },
              "successFactors": {
                "baseUrl": "https://example.invalid/odata/v2",
                "query": {
                  "entitySet": "PerPerson",
                  "identityField": "personIdExternal",
                  "deltaField": "lastModifiedDateTime",
                  "select": [ "userId" ],
                  "expand": []
                },
                "auth": {
                  "mode": "basic",
                  "basic": {
                    "username": "user",
                    "password": "pass"
                  }
                }
              },
              "sync": {
                "enableBeforeStartDays": 7,
                "deletionRetentionDays": 30
              },
              "safety": {
                "maxCreatesPerRun": 25,
                "maxDisablesPerRun": 25,
                "maxDeletionsPerRun": 25
              },
              "reporting": {
                "outputDirectory": "{{reportingPath.Replace("\\", "\\\\")}}"
              }
            }
            """);

            File.WriteAllText(mappingConfigPath, """
            {
              "mappings": [
                {
                  "source": "userId",
                  "target": "employeeId",
                  "enabled": true,
                  "required": true,
                  "transform": "identity"
                }
              ]
            }
            """);

            Environment.SetEnvironmentVariable("SYNCFACTORS_CONFIG_PATH", syncConfigPath);
            Environment.SetEnvironmentVariable("SYNCFACTORS_MAPPING_CONFIG_PATH", mappingConfigPath);
            Environment.SetEnvironmentVariable("ASPNETCORE_ENVIRONMENT", "Development");
            Environment.SetEnvironmentVariable("DOTNET_ENVIRONMENT", "Development");

            builder.UseEnvironment("Development");
            builder.ConfigureAppConfiguration((_, configBuilder) =>
            {
                configBuilder.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["SyncFactors:SqlitePath"] = sqlitePath,
                    ["SyncFactors:ConfigPath"] = syncConfigPath,
                    ["SyncFactors:MappingConfigPath"] = mappingConfigPath,
                    ["SyncFactors:Auth:BootstrapAdmin:Username"] = "bootstrap-admin",
                    ["SyncFactors:Auth:BootstrapAdmin:Password"] = "BootstrapAdmin123!"
                });
            });
        }

        public override async ValueTask DisposeAsync()
        {
            await base.DisposeAsync();

            Environment.SetEnvironmentVariable("SYNCFACTORS_CONFIG_PATH", _originalConfigPath);
            Environment.SetEnvironmentVariable("SYNCFACTORS_MAPPING_CONFIG_PATH", _originalMappingConfigPath);
            Environment.SetEnvironmentVariable("ASPNETCORE_ENVIRONMENT", _originalAspNetCoreEnvironment);
            Environment.SetEnvironmentVariable("DOTNET_ENVIRONMENT", _originalDotNetEnvironment);

            try
            {
                if (Directory.Exists(_tempRoot))
                {
                    Directory.Delete(_tempRoot, recursive: true);
                }
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }

    private sealed record HealthzResponse(string Status);
}

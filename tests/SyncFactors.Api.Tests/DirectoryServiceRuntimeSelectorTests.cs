using SyncFactors.Api;
using SyncFactors.Domain;
using SyncFactors.Infrastructure;
using Microsoft.Extensions.DependencyInjection;

namespace SyncFactors.Api.Tests;

public sealed class DirectoryServiceRuntimeSelectorTests
{
    [Fact]
    public void UseScaffoldDirectoryServices_ReturnsTrue_ForMockProfile()
    {
        var config = LoadConfig();

        Assert.True(DirectoryServiceRuntimeSelector.UseScaffoldDirectoryServices(config, "mock"));
    }

    [Fact]
    public void UseScaffoldDirectoryServices_ReturnsFalse_ForRealProfile()
    {
        var config = LoadConfig();

        Assert.False(DirectoryServiceRuntimeSelector.UseScaffoldDirectoryServices(config, "real"));
    }

    [Fact]
    public void UseScaffoldDirectoryServices_ReturnsFalse_WhenRunProfileMissing()
    {
        var config = LoadConfig();

        Assert.False(DirectoryServiceRuntimeSelector.UseScaffoldDirectoryServices(config, null));
    }

    [Fact]
    public void AddDirectoryRuntimeServices_ResolvesScaffoldServices_ForMockProfile()
    {
        var tempRoot = Directory.CreateTempSubdirectory("syncfactors-directory-services-mock").FullName;
        var previousRunProfile = Environment.GetEnvironmentVariable("SYNCFACTORS_RUN_PROFILE");

        try
        {
            Environment.SetEnvironmentVariable("SYNCFACTORS_RUN_PROFILE", "mock");
            using var serviceProvider = BuildServiceProvider(tempRoot);

            Assert.IsType<ScaffoldDirectoryGateway>(serviceProvider.GetRequiredService<IDirectoryGateway>());
            Assert.IsType<ScaffoldDirectoryCommandGateway>(serviceProvider.GetRequiredService<IDirectoryCommandGateway>());
        }
        finally
        {
            Environment.SetEnvironmentVariable("SYNCFACTORS_RUN_PROFILE", previousRunProfile);
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public void AddDirectoryRuntimeServices_ResolvesActiveDirectoryServices_ForRealProfile()
    {
        var tempRoot = Directory.CreateTempSubdirectory("syncfactors-directory-services-real").FullName;
        var previousRunProfile = Environment.GetEnvironmentVariable("SYNCFACTORS_RUN_PROFILE");

        try
        {
            Environment.SetEnvironmentVariable("SYNCFACTORS_RUN_PROFILE", "real");
            using var serviceProvider = BuildServiceProvider(tempRoot);

            Assert.IsType<ActiveDirectoryGateway>(serviceProvider.GetRequiredService<IDirectoryGateway>());
            Assert.IsType<ActiveDirectoryCommandGateway>(serviceProvider.GetRequiredService<IDirectoryCommandGateway>());
        }
        finally
        {
            Environment.SetEnvironmentVariable("SYNCFACTORS_RUN_PROFILE", previousRunProfile);
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    private static SyncFactorsConfigDocument LoadConfig()
    {
        var tempRoot = Directory.CreateTempSubdirectory("syncfactors-directory-selector").FullName;

        try
        {
            var syncConfigPath = Path.Combine(tempRoot, "sync-config.json");
            File.WriteAllText(
                syncConfigPath,
                $$"""
                {
                  "secrets": {},
                  "ad": {
                    "server": "ldaps.example.com",
                    "username": "",
                    "bindPassword": "",
                    "defaultActiveOu": "OU=LabUsers,DC=example,DC=com",
                    "prehireOu": "OU=Prehire,DC=example,DC=com",
                    "graveyardOu": "OU=LabGraveyard,DC=example,DC=com",
                    "identityAttribute": "employeeID"
                  },
                  "successFactors": {
                    "baseUrl": "http://127.0.0.1:18080/odata/v2",
                    "query": {
                      "entitySet": "EmpJob",
                      "identityField": "userId",
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
                    "deletionRetentionDays": 90
                  },
                  "safety": {
                    "maxCreatesPerRun": 25,
                    "maxDisablesPerRun": 25,
                    "maxDeletionsPerRun": 25
                  },
                  "reporting": {
                    "outputDirectory": "/tmp"
                  }
                }
                """);

            var loader = new SyncFactorsConfigurationLoader(new SyncFactorsConfigPathResolver(syncConfigPath, null));
            return loader.GetSyncConfig();
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    private static ServiceProvider BuildServiceProvider(string tempRoot)
    {
        var syncConfigPath = Path.Combine(tempRoot, "sync-config.json");
        var mappingConfigPath = Path.Combine(tempRoot, "mapping-config.json");
        var scaffoldDataPath = Path.Combine(tempRoot, "scaffold-data.json");

        File.WriteAllText(
            syncConfigPath,
            """
            {
              "secrets": {},
              "ad": {
                "server": "localhost",
                "port": 389,
                "username": "CN=svc-sync,OU=Service Accounts,DC=example,DC=com",
                "bindPassword": "password",
                "defaultActiveOu": "OU=LabUsers,DC=example,DC=com",
                "prehireOu": "OU=Prehire,DC=example,DC=com",
                "graveyardOu": "OU=LabGraveyard,DC=example,DC=com",
                "identityAttribute": "employeeID"
              },
              "successFactors": {
                "baseUrl": "http://127.0.0.1:18080/odata/v2",
                "query": {
                  "entitySet": "EmpJob",
                  "identityField": "userId",
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
                "deletionRetentionDays": 90
              },
              "safety": {
                "maxCreatesPerRun": 25,
                "maxDisablesPerRun": 25,
                "maxDeletionsPerRun": 25
              },
              "reporting": {
                "outputDirectory": "/tmp"
              }
            }
            """);
        File.WriteAllText(
            mappingConfigPath,
            """
            {
              "mappings": [
                {
                  "source": "userId",
                  "target": "employeeID",
                  "enabled": true,
                  "required": true,
                  "transform": "none"
                }
              ]
            }
            """);
        File.WriteAllText(
            scaffoldDataPath,
            """
            {
              "workers": [],
              "directoryUsers": []
            }
            """);

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(new SyncFactorsConfigPathResolver(syncConfigPath, mappingConfigPath));
        services.AddSingleton(new ScaffoldDataPathResolver(scaffoldDataPath));
        services.AddSingleton<SyncFactorsConfigurationLoader>();
        services.AddSingleton<IAttributeMappingProvider, AttributeMappingProvider>();
        services.AddSingleton<IActiveDirectoryConnectionPool, ActiveDirectoryConnectionPool>();
        services.AddSingleton<ScaffoldDataStore>();
        services.AddDirectoryRuntimeServices();

        return services.BuildServiceProvider();
    }
}

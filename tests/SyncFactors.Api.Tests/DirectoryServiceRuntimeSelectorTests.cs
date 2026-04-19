using SyncFactors.Api;
using SyncFactors.Infrastructure;

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
}

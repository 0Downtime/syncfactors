using SyncFactors.Infrastructure;

namespace SyncFactors.Infrastructure.Tests;

public sealed class SyncFactorsConfigurationLoaderTests
{
    [Fact]
    public async Task GetSyncConfig_DefaultsIdentityPolicyToggleToFalse_WhenOmitted()
    {
        var config = await LoadConfigAsync(identityPolicyJson: null);

        Assert.False(config.Ad.IdentityPolicy.ResolveCreateConflictingUpnAndMail);
    }

    [Fact]
    public async Task GetSyncConfig_LoadsIdentityPolicyToggle_WhenExplicitlyTrue()
    {
        var config = await LoadConfigAsync("""
          "identityPolicy": {
            "resolveCreateConflictingUpnAndMail": true
          },
        """);

        Assert.True(config.Ad.IdentityPolicy.ResolveCreateConflictingUpnAndMail);
    }

    [Fact]
    public async Task GetSyncConfig_LoadsIdentityPolicyToggle_WhenExplicitlyFalse()
    {
        var config = await LoadConfigAsync("""
          "identityPolicy": {
            "resolveCreateConflictingUpnAndMail": false
          },
        """);

        Assert.False(config.Ad.IdentityPolicy.ResolveCreateConflictingUpnAndMail);
    }

    [Fact]
    public async Task GetSyncConfig_DefaultsAutoDeleteFromGraveyardToFalse_WhenOmitted()
    {
        var config = await LoadConfigAsync(identityPolicyJson: null);

        Assert.False(config.Sync.AutoDeleteFromGraveyard);
    }

    [Fact]
    public async Task GetSyncConfig_LoadsAutoDeleteFromGraveyard_WhenExplicitlyTrue()
    {
        var config = await LoadConfigAsync(
            identityPolicyJson: null,
            syncJson: """
              "autoDeleteFromGraveyard": true
            """);

        Assert.True(config.Sync.AutoDeleteFromGraveyard);
    }

    private static async Task<SyncFactorsConfigDocument> LoadConfigAsync(string? identityPolicyJson, string? syncJson = null)
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "syncfactors-config-loader", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);
        var renderedSyncJson = string.IsNullOrWhiteSpace(syncJson)
            ? string.Empty
            : $",{Environment.NewLine}{syncJson.Trim()}";

        var configPath = Path.Combine(tempRoot, "sync-config.json");
        var mappingConfigPath = Path.Combine(tempRoot, "mapping-config.json");

        await File.WriteAllTextAsync(configPath, $$"""
        {
          "secrets": {
            "adServerEnv": null,
            "adUsernameEnv": null,
            "adBindPasswordEnv": null
          },
          "successFactors": {
            "baseUrl": "http://example.test/odata/v2",
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
              "select": ["personIdExternal"],
              "expand": []
            }
          },
          "ad": {
            "server": "ldap.example.test",
            "username": "",
            "bindPassword": "",
            "identityAttribute": "employeeID",
            "defaultActiveOu": "OU=LabUsers,DC=example,DC=com",
            "prehireOu": "OU=Prehire,DC=example,DC=com",
            "graveyardOu": "OU=LabGraveyard,DC=example,DC=com",
        {{identityPolicyJson ?? string.Empty}}
            "defaultPassword": "ignored-by-loader"
          },
          "sync": {
            "enableBeforeStartDays": 7,
            "deletionRetentionDays": 90{{renderedSyncJson}}
          },
          "safety": {
            "maxCreatesPerRun": 10,
            "maxDisablesPerRun": 10,
            "maxDeletionsPerRun": 10
          },
          "reporting": {
            "outputDirectory": "/tmp"
          }
        }
        """);

        await File.WriteAllTextAsync(mappingConfigPath, """
        {
          "mappings": [
            {
              "source": "personIdExternal",
              "target": "employeeID",
              "enabled": true,
              "required": true,
              "transform": "Trim"
            }
          ]
        }
        """);

        var loader = new SyncFactorsConfigurationLoader(new SyncFactorsConfigPathResolver(configPath, mappingConfigPath));
        return loader.GetSyncConfig();
    }
}

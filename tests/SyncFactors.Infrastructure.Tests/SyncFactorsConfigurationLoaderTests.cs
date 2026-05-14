using SyncFactors.Infrastructure;

namespace SyncFactors.Infrastructure.Tests;

public sealed class SyncFactorsConfigurationLoaderTests
{
    [Fact]
    public void SecretResolver_BuildsDefaultWindowsCredentialTargetName()
    {
        var originalPrefix = Environment.GetEnvironmentVariable(SyncFactorsSecretResolver.WindowsCredentialPrefixEnvironmentVariable);
        try
        {
            Environment.SetEnvironmentVariable(SyncFactorsSecretResolver.WindowsCredentialPrefixEnvironmentVariable, null);

            var targetName = SyncFactorsSecretResolver.GetWindowsCredentialTargetName("SF_AD_SYNC_AD_BIND_PASSWORD");

            Assert.Equal("SyncFactors/SF_AD_SYNC_AD_BIND_PASSWORD", targetName);
        }
        finally
        {
            Environment.SetEnvironmentVariable(SyncFactorsSecretResolver.WindowsCredentialPrefixEnvironmentVariable, originalPrefix);
        }
    }

    [Fact]
    public void SecretResolver_BuildsConfiguredWindowsCredentialTargetName()
    {
        var originalPrefix = Environment.GetEnvironmentVariable(SyncFactorsSecretResolver.WindowsCredentialPrefixEnvironmentVariable);
        try
        {
            Environment.SetEnvironmentVariable(SyncFactorsSecretResolver.WindowsCredentialPrefixEnvironmentVariable, "SyncFactors/Production/");

            var targetName = SyncFactorsSecretResolver.GetWindowsCredentialTargetName("SF_AD_SYNC_AD_BIND_PASSWORD");

            Assert.Equal("SyncFactors/Production/SF_AD_SYNC_AD_BIND_PASSWORD", targetName);
        }
        finally
        {
            Environment.SetEnvironmentVariable(SyncFactorsSecretResolver.WindowsCredentialPrefixEnvironmentVariable, originalPrefix);
        }
    }

    [Fact]
    public async Task GetSyncConfig_DefaultsIdentityPolicyToggleToTrue_WhenOmitted()
    {
        var config = await LoadConfigAsync(adJson: null);

        Assert.True(config.Ad.IdentityPolicy.ResolveCreateConflictingUpnAndMail);
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
    public async Task GetSyncConfig_DefaultsIdentityCorrelationToDisabled_WhenOmitted()
    {
        var config = await LoadConfigAsync(adJson: null);

        Assert.NotNull(config.Ad.IdentityCorrelation);
        Assert.False(config.Ad.IdentityCorrelation!.Enabled);
        Assert.Null(config.Ad.IdentityCorrelation.SuccessorPersonIdExternalAttribute);
        Assert.Null(config.Ad.IdentityCorrelation.PreviousPersonIdExternalAttribute);
    }

    [Fact]
    public async Task GetSyncConfig_LoadsIdentityCorrelation_WhenConfigured()
    {
        var config = await LoadConfigAsync("""
          "identityCorrelation": {
            "enabled": true,
            "successorPersonIdExternalAttribute": "extensionAttribute14",
            "previousPersonIdExternalAttribute": "extensionAttribute15"
          },
        """);

        Assert.True(config.Ad.IdentityCorrelation!.Enabled);
        Assert.Equal("extensionAttribute14", config.Ad.IdentityCorrelation.SuccessorPersonIdExternalAttribute);
        Assert.Equal("extensionAttribute15", config.Ad.IdentityCorrelation.PreviousPersonIdExternalAttribute);
    }

    [Fact]
    public async Task GetSyncConfig_DefaultsCreateTimeEnableWithoutPasswordProvisioningToFalse_WhenOmitted()
    {
        var config = await LoadConfigAsync(adJson: null);

        Assert.False(config.Ad.Transport.AllowCreateEnableWithoutPasswordProvisioning);
    }

    [Fact]
    public async Task GetSyncConfig_LoadsCreateTimeEnableWithoutPasswordProvisioning_WhenExplicitlyTrue()
    {
        var config = await LoadConfigAsync("""
          "transport": {
            "mode": "ldap",
            "allowCreateEnableWithoutPasswordProvisioning": true
          },
        """);

        Assert.True(config.Ad.Transport.AllowCreateEnableWithoutPasswordProvisioning);
    }

    [Fact]
    public async Task GetSyncConfig_DefaultsUpnSuffix_WhenOmitted()
    {
        var config = await LoadConfigAsync(adJson: null);

        Assert.Equal("example.test", config.Ad.UpnSuffix);
    }

    [Fact]
    public async Task GetSyncConfig_NormalizesUpnSuffix_WhenConfiguredWithAtPrefix()
    {
        var config = await LoadConfigAsync("""
          "identityPolicy": {
            "resolveCreateConflictingUpnAndMail": false
          },
          "upnSuffix": "@z.local",
        """);

        Assert.Equal("z.local", config.Ad.UpnSuffix);
    }

    [Fact]
    public async Task GetSyncConfig_LoadsLicensingGroups_WhenConfigured()
    {
        var config = await LoadConfigAsync("""
          "licensingGroups": [
            " CN=M365-E3-Prestage,OU=Groups,DC=example,DC=com ",
            "CN=VPN-Users,OU=Groups,DC=example,DC=com",
            "CN=M365-E3-Prestage,OU=Groups,DC=example,DC=com"
          ],
        """);

        Assert.Equal(
            [
                "CN=M365-E3-Prestage,OU=Groups,DC=example,DC=com",
                "CN=VPN-Users,OU=Groups,DC=example,DC=com"
            ],
            config.Ad.LicensingGroups);
    }

    [Fact]
    public async Task GetSyncConfig_DefaultsAutoDeleteFromGraveyardToFalse_WhenOmitted()
    {
        var config = await LoadConfigAsync(adJson: null);

        Assert.False(config.Sync.AutoDeleteFromGraveyard);
    }

    [Fact]
    public async Task GetSyncConfig_LoadsAutoDeleteFromGraveyard_WhenExplicitlyTrue()
    {
        var config = await LoadConfigAsync(
            adJson: null,
            syncJson: """
              "autoDeleteFromGraveyard": true
            """);

        Assert.True(config.Sync.AutoDeleteFromGraveyard);
    }

    [Fact]
    public async Task GetSyncConfig_DefaultsMaxDegreeOfParallelismToTwo_WhenOmitted()
    {
        var config = await LoadConfigAsync(adJson: null);

        Assert.Equal(2, config.Sync.MaxDegreeOfParallelism);
    }

    [Fact]
    public async Task GetSyncConfig_LoadsMaxDegreeOfParallelism_WhenExplicitlyConfigured()
    {
        var config = await LoadConfigAsync(
            adJson: null,
            syncJson: """
              "maxDegreeOfParallelism": 6
            """);

        Assert.Equal(6, config.Sync.MaxDegreeOfParallelism);
    }

    [Fact]
    public async Task GetSyncConfig_DefaultsApprovalPolicyToDisabled_WhenOmitted()
    {
        var config = await LoadConfigAsync(adJson: null);

        Assert.False(config.Approval.Enabled);
        Assert.Empty(config.Approval.RequireFor);
    }

    [Fact]
    public async Task GetSyncConfig_LoadsApprovalPolicy_WhenExplicitlyConfigured()
    {
        var config = await LoadConfigAsync(
            adJson: null,
            approvalJson: """
              "enabled": true,
              "requireFor": [
                " DisableUser ",
                "DeleteUser",
                "DisableUser"
              ]
            """);

        Assert.True(config.Approval.Enabled);
        Assert.Equal(["DisableUser", "DeleteUser"], config.Approval.RequireFor);
    }

    [Fact]
    public async Task GetSyncConfig_DefaultsRealSyncEnabledToTrue_WhenOmitted()
    {
        var config = await LoadConfigAsync(adJson: null);

        Assert.True(config.Sync.RealSyncEnabled);
    }

    [Fact]
    public async Task GetSyncConfig_LoadsRealSyncEnabled_WhenExplicitlyFalse()
    {
        var config = await LoadConfigAsync(
            adJson: null,
            syncJson: """
              "realSyncEnabled": false
            """);

        Assert.False(config.Sync.RealSyncEnabled);
    }

    [Fact]
    public async Task Loader_TrimsIdentityAttributeAndMappingIdentifiers()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "syncfactors-config-loader", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);

        var configPath = Path.Combine(tempRoot, "sync-config.json");
        var mappingConfigPath = Path.Combine(tempRoot, "mapping-config.json");

        await File.WriteAllTextAsync(configPath, """
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
            "identityAttribute": " employeeID ",
            "defaultActiveOu": "OU=LabUsers,DC=example,DC=com",
            "prehireOu": "OU=Prehire,DC=example,DC=com",
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
            "outputDirectory": "/tmp"
          }
        }
        """);

        await File.WriteAllTextAsync(mappingConfigPath, """
        {
          "mappings": [
            {
              "source": " personIdExternal ",
              "target": " employeeID ",
              "enabled": true,
              "required": true,
              "transform": " Trim "
            }
          ]
        }
        """);

        var loader = new SyncFactorsConfigurationLoader(new SyncFactorsConfigPathResolver(configPath, mappingConfigPath));
        var config = loader.GetSyncConfig();
        var mapping = loader.GetMappingConfig();

        Assert.Equal("employeeID", config.Ad.IdentityAttribute);
        var attributeMapping = Assert.Single(mapping.Mappings);
        Assert.Equal("personIdExternal", attributeMapping.Source);
        Assert.Equal("employeeID", attributeMapping.Target);
        Assert.Equal("Trim", attributeMapping.Transform);
    }

    [Fact]
    public async Task GetSyncConfig_LoadsSecretsFromResolver_WhenEnvironmentValuesAreAbsent()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "syncfactors-config-loader", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);

        var configPath = Path.Combine(tempRoot, "sync-config.json");
        var mappingConfigPath = Path.Combine(tempRoot, "mapping-config.json");

        await File.WriteAllTextAsync(configPath, """
        {
          "secrets": {
            "successFactorsUsernameEnv": "SYNCFACTORS_TEST_SF_USERNAME",
            "successFactorsPasswordEnv": "SYNCFACTORS_TEST_SF_PASSWORD",
            "adServerEnv": "SYNCFACTORS_TEST_AD_SERVER",
            "adUsernameEnv": "SYNCFACTORS_TEST_AD_USERNAME",
            "adBindPasswordEnv": "SYNCFACTORS_TEST_AD_BIND_PASSWORD"
          },
          "successFactors": {
            "baseUrl": "http://example.test/odata/v2",
            "auth": {
              "mode": "basic",
              "basic": {
                "username": "",
                "password": ""
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
            "server": "",
            "username": "",
            "bindPassword": "",
            "identityAttribute": "employeeID",
            "defaultActiveOu": "OU=LabUsers,DC=example,DC=com",
            "prehireOu": "OU=Prehire,DC=example,DC=com",
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

        var secretResolver = new DictionarySecretResolver(new Dictionary<string, string>
        {
            ["SYNCFACTORS_TEST_SF_USERNAME"] = "sf-user",
            ["SYNCFACTORS_TEST_SF_PASSWORD"] = "sf-password",
            ["SYNCFACTORS_TEST_AD_SERVER"] = "dc01.example.test",
            ["SYNCFACTORS_TEST_AD_USERNAME"] = "svc-syncfactors-adbind@example.test",
            ["SYNCFACTORS_TEST_AD_BIND_PASSWORD"] = "ad-password"
        });

        var loader = new SyncFactorsConfigurationLoader(
            new SyncFactorsConfigPathResolver(configPath, mappingConfigPath),
            secretResolver);

        var config = loader.GetSyncConfig();

        Assert.Equal("sf-user", config.SuccessFactors.Auth.Basic!.Username);
        Assert.Equal("sf-password", config.SuccessFactors.Auth.Basic.Password);
        Assert.Equal("dc01.example.test", config.Ad.Server);
        Assert.Equal("svc-syncfactors-adbind@example.test", config.Ad.Username);
        Assert.Equal("ad-password", config.Ad.BindPassword);
    }

    private static async Task<SyncFactorsConfigDocument> LoadConfigAsync(string? adJson, string? syncJson = null, string? approvalJson = null)
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "syncfactors-config-loader", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);
        var renderedAdJson = string.IsNullOrWhiteSpace(adJson)
            ? string.Empty
            : $"{Environment.NewLine}{adJson.Trim()}";
        var renderedSyncJson = string.IsNullOrWhiteSpace(syncJson)
            ? string.Empty
            : $",{Environment.NewLine}{syncJson.Trim()}";
        var renderedApprovalJson = string.IsNullOrWhiteSpace(approvalJson)
            ? string.Empty
            : $",{Environment.NewLine}          \"approval\": {{{Environment.NewLine}{approvalJson.Trim()}{Environment.NewLine}          }}";

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
        {{renderedAdJson}}
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
          }{{renderedApprovalJson}},
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

    private sealed class DictionarySecretResolver(IReadOnlyDictionary<string, string> secrets) : ISyncFactorsSecretResolver
    {
        public string? GetSecretValue(string? variableName) =>
            !string.IsNullOrWhiteSpace(variableName) && secrets.TryGetValue(variableName, out var value)
                ? value
                : null;

        public string ResolveSourceLabel(string? variableName, string fallbackSource) =>
            !string.IsNullOrWhiteSpace(variableName) && secrets.ContainsKey(variableName)
                ? $"test secret ({variableName})"
                : fallbackSource;
    }
}

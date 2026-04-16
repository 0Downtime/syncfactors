using SyncFactors.Infrastructure;

namespace SyncFactors.Infrastructure.Tests;

public sealed class SyncFactorsConfigurationValidatorTests
{
    [Fact]
    public async Task Validate_AllowsEnvironmentBackedSecretsOutsideDevelopment_WhenJsonSecretFieldsAreBlank()
    {
        var tempRoot = CreateTempRoot();
        var configPath = await WriteConfigAsync(
            tempRoot,
            successFactorsUsernameLiteral: "",
            successFactorsPasswordLiteral: "",
            adBindPasswordLiteral: "");
        var mappingConfigPath = await WriteMappingConfigAsync(tempRoot);

        var originalDotnetEnvironment = Environment.GetEnvironmentVariable("DOTNET_ENVIRONMENT");
        var originalSfUsername = Environment.GetEnvironmentVariable("SF_AD_SYNC_SF_USERNAME");
        var originalSfPassword = Environment.GetEnvironmentVariable("SF_AD_SYNC_SF_PASSWORD");
        var originalAdBindPassword = Environment.GetEnvironmentVariable("SF_AD_SYNC_AD_BIND_PASSWORD");

        try
        {
            Environment.SetEnvironmentVariable("DOTNET_ENVIRONMENT", "Production");
            Environment.SetEnvironmentVariable("SF_AD_SYNC_SF_USERNAME", "env-backed-user");
            Environment.SetEnvironmentVariable("SF_AD_SYNC_SF_PASSWORD", "env-backed-password");
            Environment.SetEnvironmentVariable("SF_AD_SYNC_AD_BIND_PASSWORD", "env-backed-bind-password");

            var loader = new SyncFactorsConfigurationLoader(new SyncFactorsConfigPathResolver(configPath, mappingConfigPath));
            var validator = new SyncFactorsConfigurationValidator(loader);

            validator.Validate();
        }
        finally
        {
            Environment.SetEnvironmentVariable("DOTNET_ENVIRONMENT", originalDotnetEnvironment);
            Environment.SetEnvironmentVariable("SF_AD_SYNC_SF_USERNAME", originalSfUsername);
            Environment.SetEnvironmentVariable("SF_AD_SYNC_SF_PASSWORD", originalSfPassword);
            Environment.SetEnvironmentVariable("SF_AD_SYNC_AD_BIND_PASSWORD", originalAdBindPassword);
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public async Task Validate_RejectsLiteralSuccessFactorsPasswordOutsideDevelopment()
    {
        var tempRoot = CreateTempRoot();
        var configPath = await WriteConfigAsync(
            tempRoot,
            successFactorsUsernameLiteral: "",
            successFactorsPasswordLiteral: "literal-password",
            adBindPasswordLiteral: "");
        var mappingConfigPath = await WriteMappingConfigAsync(tempRoot);

        var originalDotnetEnvironment = Environment.GetEnvironmentVariable("DOTNET_ENVIRONMENT");
        var originalSfUsername = Environment.GetEnvironmentVariable("SF_AD_SYNC_SF_USERNAME");
        var originalSfPassword = Environment.GetEnvironmentVariable("SF_AD_SYNC_SF_PASSWORD");

        try
        {
            Environment.SetEnvironmentVariable("DOTNET_ENVIRONMENT", "Production");
            Environment.SetEnvironmentVariable("SF_AD_SYNC_SF_USERNAME", "env-backed-user");
            Environment.SetEnvironmentVariable("SF_AD_SYNC_SF_PASSWORD", null);

            var loader = new SyncFactorsConfigurationLoader(new SyncFactorsConfigPathResolver(configPath, mappingConfigPath));
            var validator = new SyncFactorsConfigurationValidator(loader);

            var exception = Assert.Throws<InvalidOperationException>(() => validator.Validate());

            Assert.Equal("Production config must not include a literal SuccessFactors password.", exception.Message);
        }
        finally
        {
            Environment.SetEnvironmentVariable("DOTNET_ENVIRONMENT", originalDotnetEnvironment);
            Environment.SetEnvironmentVariable("SF_AD_SYNC_SF_USERNAME", originalSfUsername);
            Environment.SetEnvironmentVariable("SF_AD_SYNC_SF_PASSWORD", originalSfPassword);
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Theory]
    [InlineData("localhost")]
    [InlineData("127.0.0.1")]
    [InlineData("::1")]
    public async Task Validate_RejectsLoopbackAdServerOutsideDevelopment(string adServer)
    {
        var tempRoot = CreateTempRoot();
        var configPath = await WriteConfigAsync(
            tempRoot,
            successFactorsUsernameLiteral: "",
            successFactorsPasswordLiteral: "",
            adBindPasswordLiteral: "",
            adServer: adServer);
        var mappingConfigPath = await WriteMappingConfigAsync(tempRoot);

        var originalDotnetEnvironment = Environment.GetEnvironmentVariable("DOTNET_ENVIRONMENT");
        var originalSfUsername = Environment.GetEnvironmentVariable("SF_AD_SYNC_SF_USERNAME");
        var originalSfPassword = Environment.GetEnvironmentVariable("SF_AD_SYNC_SF_PASSWORD");
        var originalAdBindPassword = Environment.GetEnvironmentVariable("SF_AD_SYNC_AD_BIND_PASSWORD");

        try
        {
            Environment.SetEnvironmentVariable("DOTNET_ENVIRONMENT", "Production");
            Environment.SetEnvironmentVariable("SF_AD_SYNC_SF_USERNAME", "env-backed-user");
            Environment.SetEnvironmentVariable("SF_AD_SYNC_SF_PASSWORD", "env-backed-password");
            Environment.SetEnvironmentVariable("SF_AD_SYNC_AD_BIND_PASSWORD", "env-backed-bind-password");

            var loader = new SyncFactorsConfigurationLoader(new SyncFactorsConfigPathResolver(configPath, mappingConfigPath));
            var validator = new SyncFactorsConfigurationValidator(loader);

            var exception = Assert.Throws<InvalidOperationException>(() => validator.Validate());

            Assert.Equal("SyncFactors AD server must not resolve to localhost or a loopback address outside Development.", exception.Message);
        }
        finally
        {
            Environment.SetEnvironmentVariable("DOTNET_ENVIRONMENT", originalDotnetEnvironment);
            Environment.SetEnvironmentVariable("SF_AD_SYNC_SF_USERNAME", originalSfUsername);
            Environment.SetEnvironmentVariable("SF_AD_SYNC_SF_PASSWORD", originalSfPassword);
            Environment.SetEnvironmentVariable("SF_AD_SYNC_AD_BIND_PASSWORD", originalAdBindPassword);
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public async Task Validate_AllowsLdapFallbackOutsideDevelopment_WhenSecurePrimaryTransportRemainsConfigured()
    {
        var tempRoot = CreateTempRoot();
        var configPath = await WriteConfigAsync(
            tempRoot,
            successFactorsUsernameLiteral: "",
            successFactorsPasswordLiteral: "",
            adBindPasswordLiteral: "",
            allowLdapFallback: true);
        var mappingConfigPath = await WriteMappingConfigAsync(tempRoot);

        var originalDotnetEnvironment = Environment.GetEnvironmentVariable("DOTNET_ENVIRONMENT");
        var originalSfUsername = Environment.GetEnvironmentVariable("SF_AD_SYNC_SF_USERNAME");
        var originalSfPassword = Environment.GetEnvironmentVariable("SF_AD_SYNC_SF_PASSWORD");
        var originalAdBindPassword = Environment.GetEnvironmentVariable("SF_AD_SYNC_AD_BIND_PASSWORD");

        try
        {
            Environment.SetEnvironmentVariable("DOTNET_ENVIRONMENT", "Production");
            Environment.SetEnvironmentVariable("SF_AD_SYNC_SF_USERNAME", "env-backed-user");
            Environment.SetEnvironmentVariable("SF_AD_SYNC_SF_PASSWORD", "env-backed-password");
            Environment.SetEnvironmentVariable("SF_AD_SYNC_AD_BIND_PASSWORD", "env-backed-bind-password");

            var loader = new SyncFactorsConfigurationLoader(new SyncFactorsConfigPathResolver(configPath, mappingConfigPath));
            var validator = new SyncFactorsConfigurationValidator(loader);

            validator.Validate();
        }
        finally
        {
            Environment.SetEnvironmentVariable("DOTNET_ENVIRONMENT", originalDotnetEnvironment);
            Environment.SetEnvironmentVariable("SF_AD_SYNC_SF_USERNAME", originalSfUsername);
            Environment.SetEnvironmentVariable("SF_AD_SYNC_SF_PASSWORD", originalSfPassword);
            Environment.SetEnvironmentVariable("SF_AD_SYNC_AD_BIND_PASSWORD", originalAdBindPassword);
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public async Task Validate_RejectsLdapFallbackWhenPrimaryModeIsAlreadyLdap()
    {
        var tempRoot = CreateTempRoot();
        var configPath = await WriteConfigAsync(
            tempRoot,
            successFactorsUsernameLiteral: "",
            successFactorsPasswordLiteral: "",
            adBindPasswordLiteral: "",
            transportMode: "ldap",
            allowLdapFallback: true);
        var mappingConfigPath = await WriteMappingConfigAsync(tempRoot);

        var originalDotnetEnvironment = Environment.GetEnvironmentVariable("DOTNET_ENVIRONMENT");
        var originalSfUsername = Environment.GetEnvironmentVariable("SF_AD_SYNC_SF_USERNAME");
        var originalSfPassword = Environment.GetEnvironmentVariable("SF_AD_SYNC_SF_PASSWORD");
        var originalAdBindPassword = Environment.GetEnvironmentVariable("SF_AD_SYNC_AD_BIND_PASSWORD");

        try
        {
            Environment.SetEnvironmentVariable("DOTNET_ENVIRONMENT", "Production");
            Environment.SetEnvironmentVariable("SF_AD_SYNC_SF_USERNAME", "env-backed-user");
            Environment.SetEnvironmentVariable("SF_AD_SYNC_SF_PASSWORD", "env-backed-password");
            Environment.SetEnvironmentVariable("SF_AD_SYNC_AD_BIND_PASSWORD", "env-backed-bind-password");

            var loader = new SyncFactorsConfigurationLoader(new SyncFactorsConfigPathResolver(configPath, mappingConfigPath));
            var validator = new SyncFactorsConfigurationValidator(loader);

            var exception = Assert.Throws<InvalidOperationException>(() => validator.Validate());

            Assert.Equal("SyncFactors AD transport.allowLdapFallback cannot be enabled when transport.mode is already 'ldap'.", exception.Message);
        }
        finally
        {
            Environment.SetEnvironmentVariable("DOTNET_ENVIRONMENT", originalDotnetEnvironment);
            Environment.SetEnvironmentVariable("SF_AD_SYNC_SF_USERNAME", originalSfUsername);
            Environment.SetEnvironmentVariable("SF_AD_SYNC_SF_PASSWORD", originalSfPassword);
            Environment.SetEnvironmentVariable("SF_AD_SYNC_AD_BIND_PASSWORD", originalAdBindPassword);
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public async Task Validate_RejectsNonPositiveMaxDegreeOfParallelism()
    {
        var tempRoot = CreateTempRoot();
        var configPath = Path.Combine(tempRoot, "sync-config.json");
        var mappingConfigPath = await WriteMappingConfigAsync(tempRoot);

        await File.WriteAllTextAsync(configPath, """
        {
          "secrets": {
            "successFactorsUsernameEnv": "SF_AD_SYNC_SF_USERNAME",
            "successFactorsPasswordEnv": "SF_AD_SYNC_SF_PASSWORD",
            "adServerEnv": null,
            "adUsernameEnv": null,
            "adBindPasswordEnv": "SF_AD_SYNC_AD_BIND_PASSWORD"
          },
          "successFactors": {
            "baseUrl": "https://example.test/odata/v2",
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
            "server": "ldap.example.test",
            "username": "",
            "bindPassword": "",
            "identityAttribute": "employeeID",
            "defaultActiveOu": "OU=LabUsers,DC=example,DC=com",
            "prehireOu": "OU=Prehire,DC=example,DC=com",
            "graveyardOu": "OU=LabGraveyard,DC=example,DC=com",
            "transport": {
              "mode": "ldaps",
              "allowLdapFallback": false,
              "requireCertificateValidation": true,
              "requireSigning": true,
              "trustedCertificateThumbprints": []
            }
          },
          "sync": {
            "enableBeforeStartDays": 7,
            "deletionRetentionDays": 90,
            "maxDegreeOfParallelism": 0
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

        var originalDotnetEnvironment = Environment.GetEnvironmentVariable("DOTNET_ENVIRONMENT");
        var originalSfUsername = Environment.GetEnvironmentVariable("SF_AD_SYNC_SF_USERNAME");
        var originalSfPassword = Environment.GetEnvironmentVariable("SF_AD_SYNC_SF_PASSWORD");
        var originalAdBindPassword = Environment.GetEnvironmentVariable("SF_AD_SYNC_AD_BIND_PASSWORD");

        try
        {
            Environment.SetEnvironmentVariable("DOTNET_ENVIRONMENT", "Production");
            Environment.SetEnvironmentVariable("SF_AD_SYNC_SF_USERNAME", "env-backed-user");
            Environment.SetEnvironmentVariable("SF_AD_SYNC_SF_PASSWORD", "env-backed-password");
            Environment.SetEnvironmentVariable("SF_AD_SYNC_AD_BIND_PASSWORD", "env-backed-bind-password");

            var loader = new SyncFactorsConfigurationLoader(new SyncFactorsConfigPathResolver(configPath, mappingConfigPath));
            var validator = new SyncFactorsConfigurationValidator(loader);

            var exception = Assert.Throws<InvalidOperationException>(() => validator.Validate());

            Assert.Equal("SyncFactors sync.maxDegreeOfParallelism must be positive.", exception.Message);
        }
        finally
        {
            Environment.SetEnvironmentVariable("DOTNET_ENVIRONMENT", originalDotnetEnvironment);
            Environment.SetEnvironmentVariable("SF_AD_SYNC_SF_USERNAME", originalSfUsername);
            Environment.SetEnvironmentVariable("SF_AD_SYNC_SF_PASSWORD", originalSfPassword);
            Environment.SetEnvironmentVariable("SF_AD_SYNC_AD_BIND_PASSWORD", originalAdBindPassword);
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    private static string CreateTempRoot()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "syncfactors-config-validator", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);
        return tempRoot;
    }

    private static async Task<string> WriteConfigAsync(
        string tempRoot,
        string successFactorsUsernameLiteral,
        string successFactorsPasswordLiteral,
        string adBindPasswordLiteral,
        string adServer = "ldap.example.test",
        string transportMode = "ldaps",
        bool allowLdapFallback = false)
    {
        var configPath = Path.Combine(tempRoot, "sync-config.json");

        await File.WriteAllTextAsync(configPath, $$"""
        {
          "secrets": {
            "successFactorsUsernameEnv": "SF_AD_SYNC_SF_USERNAME",
            "successFactorsPasswordEnv": "SF_AD_SYNC_SF_PASSWORD",
            "adServerEnv": null,
            "adUsernameEnv": null,
            "adBindPasswordEnv": "SF_AD_SYNC_AD_BIND_PASSWORD"
          },
          "successFactors": {
            "baseUrl": "https://example.test/odata/v2",
            "auth": {
              "mode": "basic",
              "basic": {
                "username": "{{successFactorsUsernameLiteral}}",
                "password": "{{successFactorsPasswordLiteral}}"
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
            "server": "{{adServer}}",
            "username": "",
            "bindPassword": "{{adBindPasswordLiteral}}",
            "identityAttribute": "employeeID",
            "defaultActiveOu": "OU=LabUsers,DC=example,DC=com",
            "prehireOu": "OU=Prehire,DC=example,DC=com",
            "graveyardOu": "OU=LabGraveyard,DC=example,DC=com",
            "transport": {
              "mode": "{{transportMode}}",
              "allowLdapFallback": {{allowLdapFallback.ToString().ToLowerInvariant()}},
              "requireCertificateValidation": true,
              "requireSigning": true,
              "trustedCertificateThumbprints": []
            },
            "defaultPassword": "ignored-by-loader"
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

        return configPath;
    }

    private static async Task<string> WriteMappingConfigAsync(string tempRoot)
    {
        var mappingConfigPath = Path.Combine(tempRoot, "mapping-config.json");

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

        return mappingConfigPath;
    }
}

using System.Security.Claims;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Options;
using SyncFactors.Api;
using SyncFactors.Api.Pages.Admin;
using SyncFactors.Infrastructure;

namespace SyncFactors.Api.Tests;

public sealed class AdminConfigurationModelTests
{
    [Fact]
    public void OnGet_LoadsSectionsForAdminUsers()
    {
        using var fixture = AdminConfigurationTestFixture.Create();
        var model = CreateModel(fixture.Builder);

        model.OnGet();

        Assert.Equal(6, model.Snapshot.Sections.Count);
        Assert.Contains(model.Snapshot.Sections, section => section.Title == "Deployment");
        Assert.Contains(model.Snapshot.Sections, section => section.Title == "Attribute Mappings");
    }

    [Theory]
    [InlineData("local-break-glass", "Local break-glass only", "Enabled")]
    [InlineData("oidc", "SSO only", "Disabled")]
    [InlineData("hybrid", "SSO + break-glass", "Enabled")]
    public void OnGet_ReflectsConfiguredAuthenticationMode(string mode, string expectedLabel, string expectedBreakGlass)
    {
        using var fixture = AdminConfigurationTestFixture.Create(authOverrides: new Dictionary<string, string?>
        {
            ["SyncFactors:Auth:Mode"] = mode,
            ["SyncFactors:Auth:LocalBreakGlass:Enabled"] = mode == "oidc" ? "false" : "true"
        });
        var model = CreateModel(fixture.Builder);

        model.OnGet();

        var authSection = model.Snapshot.Sections.Single(section => section.Title == "Authentication");
        var sessionGroup = authSection.Groups.Single(group => group.Title == "Session");

        Assert.Equal(
            expectedLabel,
            sessionGroup.Entries.Single(entry => entry.Label == "Authentication mode").DisplayValue);
        Assert.Equal(
            expectedBreakGlass,
            sessionGroup.Entries.Single(entry => entry.Label == "Local break-glass access").DisplayValue);
    }

    private static ConfigurationModel CreateModel(AdminConfigurationSnapshotBuilder builder)
    {
        return new ConfigurationModel(builder)
        {
            PageContext = new PageContext
            {
                HttpContext = new DefaultHttpContext
                {
                    User = new ClaimsPrincipal(new ClaimsIdentity(
                    [
                        new Claim(ClaimTypes.NameIdentifier, "admin-1"),
                        new Claim(ClaimTypes.Name, "admin"),
                        new Claim(ClaimTypes.Role, "Admin")
                    ], "Cookies"))
                }
            }
        };
    }
}

public sealed class AdminConfigurationSnapshotBuilderTests
{
    [Fact]
    public void Build_HidesSecretValuesAndCapturesSources()
    {
        using var fixture = AdminConfigurationTestFixture.Create();

        var snapshot = fixture.Builder.Build();

        var successFactorsSection = snapshot.Sections.Single(section => section.Title == "SuccessFactors");
        var basicGroup = successFactorsSection.Groups.Single(group => group.Title == "Basic Auth");
        var username = basicGroup.Entries.Single(entry => entry.Label == "Username");
        var password = basicGroup.Entries.Single(entry => entry.Label == "Password");

        Assert.Equal("Hidden", username.DisplayValue);
        Assert.Equal(fixture.SuccessFactorsUsernameEnvName, username.SourceLabel);
        Assert.True(username.IsSecretOmitted);
        Assert.Equal("Hidden", password.DisplayValue);
        Assert.Equal(fixture.SuccessFactorsPasswordEnvName, password.SourceLabel);
        Assert.True(password.IsSecretOmitted);
    }

    [Fact]
    public void Build_UsesDefaultSourceLabelsWhenHostValuesAreUnset()
    {
        using var fixture = AdminConfigurationTestFixture.Create(includeHostToggles: false);

        var snapshot = fixture.Builder.Build();
        var deploymentSection = snapshot.Sections.Single(section => section.Title == "Deployment");
        var deploymentGroup = deploymentSection.Groups.Single();

        Assert.Equal(
            "Default",
            deploymentGroup.Entries.Single(entry => entry.Label == "Realtime updates").SourceLabel);
        Assert.Equal(
            "Default",
            deploymentGroup.Entries.Single(entry => entry.Label == "Dashboard health probes").SourceLabel);
        Assert.Equal(
            "Default",
            deploymentGroup.Entries.Single(entry => entry.Label == "Application Insights").SourceLabel);
    }

    [Fact]
    public void Build_ComputesMappingSummaryCounts()
    {
        using var fixture = AdminConfigurationTestFixture.Create();

        var snapshot = fixture.Builder.Build();
        var mappingSection = snapshot.Sections.Single(section => section.Title == "Attribute Mappings");

        Assert.NotNull(mappingSection.MappingSummary);
        Assert.Equal(3, mappingSection.MappingSummary!.TotalCount);
        Assert.Equal(2, mappingSection.MappingSummary.EnabledCount);
        Assert.Equal(1, mappingSection.MappingSummary.DisabledCount);
        Assert.Equal(1, mappingSection.MappingSummary.RequiredCount);
        Assert.Equal(3, mappingSection.MappingRows!.Count);
    }

    [Fact]
    public void Build_ShowsPreviewQueryAsNotConfigured_WhenAbsent()
    {
        using var fixture = AdminConfigurationTestFixture.Create(includePreviewQuery: false);

        var snapshot = fixture.Builder.Build();
        var successFactorsSection = snapshot.Sections.Single(section => section.Title == "SuccessFactors");
        var previewGroup = successFactorsSection.Groups.Single(group => group.Title == "Preview query");

        Assert.Equal("Not configured", previewGroup.Entries.Single().DisplayValue);
    }

    [Fact]
    public void Build_UsesConfiguredPathsAndExplicitRunProfile()
    {
        using var fixture = AdminConfigurationTestFixture.Create(runProfile: "real");

        var snapshot = fixture.Builder.Build();
        var deploymentSection = snapshot.Sections.Single(section => section.Title == "Deployment");
        var deploymentGroup = deploymentSection.Groups.Single();

        Assert.Equal("real", deploymentGroup.Entries.Single(entry => entry.Label == "Run profile").DisplayValue);
        Assert.Contains(
            fixture.SyncConfigPath,
            deploymentGroup.Entries.Single(entry => entry.Label == "Sync config path").DisplayValue);
        Assert.Equal(
            "Environment/AppSettings",
            deploymentGroup.Entries.Single(entry => entry.Label == "Run profile").SourceLabel);
    }

    [Fact]
    public void Build_UsesOauthSectionWhenConfigured()
    {
        using var fixture = AdminConfigurationTestFixture.Create(successFactorsAuthMode: "oauth");

        var snapshot = fixture.Builder.Build();
        var successFactorsSection = snapshot.Sections.Single(section => section.Title == "SuccessFactors");
        var oauthGroup = successFactorsSection.Groups.Single(group => group.Title == "OAuth");

        Assert.Equal(
            fixture.SuccessFactorsClientIdValue,
            oauthGroup.Entries.Single(entry => entry.Label == "Client ID").DisplayValue);
        Assert.Equal(
            "Hidden",
            oauthGroup.Entries.Single(entry => entry.Label == "Client secret").DisplayValue);
    }
}

internal sealed class AdminConfigurationTestFixture : IDisposable
{
    private readonly string _tempRoot;
    private readonly Dictionary<string, string?> _environmentBackup = new(StringComparer.Ordinal);

    private AdminConfigurationTestFixture(
        string tempRoot,
        AdminConfigurationSnapshotBuilder builder,
        string syncConfigPath,
        string successFactorsUsernameEnvName,
        string successFactorsPasswordEnvName,
        string successFactorsClientIdValue)
    {
        _tempRoot = tempRoot;
        Builder = builder;
        SyncConfigPath = syncConfigPath;
        SuccessFactorsUsernameEnvName = successFactorsUsernameEnvName;
        SuccessFactorsPasswordEnvName = successFactorsPasswordEnvName;
        SuccessFactorsClientIdValue = successFactorsClientIdValue;
    }

    public AdminConfigurationSnapshotBuilder Builder { get; }

    public string SyncConfigPath { get; }

    public string SuccessFactorsUsernameEnvName { get; }

    public string SuccessFactorsPasswordEnvName { get; }

    public string SuccessFactorsClientIdValue { get; }

    public static AdminConfigurationTestFixture Create(
        bool includePreviewQuery = true,
        bool includeHostToggles = true,
        string runProfile = "mock",
        string successFactorsAuthMode = "basic",
        IDictionary<string, string?>? authOverrides = null)
    {
        var tempRoot = Directory.CreateTempSubdirectory("syncfactors-admin-config").FullName;
        var syncConfigPath = Path.Combine(tempRoot, "local.sync-config.json");
        var mappingConfigPath = Path.Combine(tempRoot, "local.mapping-config.json");
        var sqlitePath = Path.Combine(tempRoot, "state", "runtime", "syncfactors.db");
        var scaffoldPath = Path.Combine(tempRoot, "config", "scaffold-data.json");

        Directory.CreateDirectory(Path.GetDirectoryName(sqlitePath)!);
        Directory.CreateDirectory(Path.GetDirectoryName(scaffoldPath)!);
        File.WriteAllText(sqlitePath, string.Empty);
        File.WriteAllText(scaffoldPath, "{ }");

        var successFactorsUsernameEnvName = $"SYNCFACTORS_TEST_SF_USER_{Guid.NewGuid():N}";
        var successFactorsPasswordEnvName = $"SYNCFACTORS_TEST_SF_PASS_{Guid.NewGuid():N}";
        var successFactorsClientIdEnvName = $"SYNCFACTORS_TEST_SF_CLIENT_ID_{Guid.NewGuid():N}";
        var successFactorsClientSecretEnvName = $"SYNCFACTORS_TEST_SF_CLIENT_SECRET_{Guid.NewGuid():N}";
        var adServerEnvName = $"SYNCFACTORS_TEST_AD_SERVER_{Guid.NewGuid():N}";
        var adUsernameEnvName = $"SYNCFACTORS_TEST_AD_USERNAME_{Guid.NewGuid():N}";
        var adPasswordEnvName = $"SYNCFACTORS_TEST_AD_PASSWORD_{Guid.NewGuid():N}";

        var successFactorsUsernameValue = "sf-service-user";
        var successFactorsPasswordValue = "Password1234!";
        var successFactorsClientIdValue = "sf-client-id";
        var successFactorsClientSecretValue = "sf-client-secret";
        var adServerValue = "ldaps.example.com";
        var adUsernameValue = "svc_sync@example.com";
        var adPasswordValue = "AnotherPassword123!";

        var builder = CreateBuilder(
            syncConfigPath,
            mappingConfigPath,
            sqlitePath,
            scaffoldPath,
            runProfile,
            successFactorsAuthMode,
            includeHostToggles,
            authOverrides);

        var fixture = new AdminConfigurationTestFixture(
            tempRoot,
            builder,
            syncConfigPath,
            successFactorsUsernameEnvName,
            successFactorsPasswordEnvName,
            successFactorsClientIdValue);

        fixture.SetEnvironment(successFactorsUsernameEnvName, successFactorsUsernameValue);
        fixture.SetEnvironment(successFactorsPasswordEnvName, successFactorsPasswordValue);
        fixture.SetEnvironment(successFactorsClientIdEnvName, successFactorsClientIdValue);
        fixture.SetEnvironment(successFactorsClientSecretEnvName, successFactorsClientSecretValue);
        fixture.SetEnvironment(adServerEnvName, adServerValue);
        fixture.SetEnvironment(adUsernameEnvName, adUsernameValue);
        fixture.SetEnvironment(adPasswordEnvName, adPasswordValue);

        File.WriteAllText(
            syncConfigPath,
            BuildSyncConfigJson(
                successFactorsAuthMode,
                includePreviewQuery,
                successFactorsUsernameEnvName,
                successFactorsPasswordEnvName,
                successFactorsClientIdEnvName,
                successFactorsClientSecretEnvName,
                adServerEnvName,
                adUsernameEnvName,
                adPasswordEnvName));
        File.WriteAllText(mappingConfigPath, BuildMappingConfigJson());

        return fixture;
    }

    public void Dispose()
    {
        foreach (var item in _environmentBackup)
        {
            Environment.SetEnvironmentVariable(item.Key, item.Value);
        }

        if (Directory.Exists(_tempRoot))
        {
            Directory.Delete(_tempRoot, recursive: true);
        }
    }

    private static AdminConfigurationSnapshotBuilder CreateBuilder(
        string syncConfigPath,
        string mappingConfigPath,
        string sqlitePath,
        string scaffoldPath,
        string runProfile,
        string successFactorsAuthMode,
        bool includeHostToggles,
        IDictionary<string, string?>? authOverrides)
    {
        var settings = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
        {
            ["SYNCFACTORS_RUN_PROFILE"] = runProfile,
            ["SyncFactors:ConfigPath"] = syncConfigPath,
            ["SyncFactors:MappingConfigPath"] = mappingConfigPath,
            ["SyncFactors:SqlitePath"] = sqlitePath,
            ["SyncFactors:ScaffoldDataPath"] = scaffoldPath,
            ["SyncFactors:Auth:Mode"] = successFactorsAuthMode == "oauth" ? "oidc" : "local-break-glass",
            ["SyncFactors:Auth:AbsoluteSessionHours"] = "12",
            ["SyncFactors:Auth:IdleTimeoutMinutes"] = "20",
            ["SyncFactors:Auth:RememberMeSessionHours"] = "8",
            ["SyncFactors:Auth:AllowRememberMe"] = "true",
            ["SyncFactors:Auth:BootstrapAdmin:Username"] = "bootstrap-admin",
            ["SyncFactors:Auth:BootstrapAdmin:Password"] = "BootstrapPassword123!",
            ["SyncFactors:Auth:LocalBreakGlass:Enabled"] = successFactorsAuthMode == "oauth" ? "false" : "true",
            ["SyncFactors:Auth:Oidc:Authority"] = "https://login.example.com/tenant/v2.0",
            ["SyncFactors:Auth:Oidc:ClientId"] = "portal-client-id",
            ["SyncFactors:Auth:Oidc:ClientSecret"] = "portal-client-secret",
            ["SyncFactors:Auth:Oidc:CallbackPath"] = "/signin-oidc",
            ["SyncFactors:Auth:Oidc:SignedOutCallbackPath"] = "/signout-callback-oidc",
            ["SyncFactors:Auth:Oidc:RolesClaimType"] = "groups",
            ["SyncFactors:Auth:Oidc:DisplayNameClaimType"] = "name",
            ["SyncFactors:Auth:Oidc:UsernameClaimType"] = "preferred_username",
            ["SyncFactors:Auth:Oidc:ViewerGroups:0"] = "viewer-group",
            ["SyncFactors:Auth:Oidc:OperatorGroups:0"] = "operator-group",
            ["SyncFactors:Auth:Oidc:AdminGroups:0"] = "admin-group"
        };

        if (includeHostToggles)
        {
            settings["SyncFactors:Realtime:Enabled"] = "false";
            settings["SyncFactors:Dashboard:HealthProbes:Enabled"] = "false";
            settings["SyncFactors:SecurityHeaders:EnableContentSecurityPolicy"] = "true";
            settings["ApplicationInsights:ConnectionString"] = "InstrumentationKey=fake";
        }

        if (authOverrides is not null)
        {
            foreach (var item in authOverrides)
            {
                settings[item.Key] = item.Value;
            }
        }

        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(settings)
            .Build();
        var options = Options.Create(config.GetSection("SyncFactors:Auth").Get<LocalAuthOptions>() ?? new LocalAuthOptions());
        var loader = new SyncFactorsConfigurationLoader(new SyncFactorsConfigPathResolver(syncConfigPath, mappingConfigPath));

        return new AdminConfigurationSnapshotBuilder(
            loader,
            new SyncFactorsConfigPathResolver(syncConfigPath, mappingConfigPath),
            new ScaffoldDataPathResolver(scaffoldPath),
            new SqlitePathResolver(sqlitePath),
            config,
            options,
            new StubWebHostEnvironment());
    }

    private void SetEnvironment(string name, string value)
    {
        if (!_environmentBackup.ContainsKey(name))
        {
            _environmentBackup[name] = Environment.GetEnvironmentVariable(name);
        }

        Environment.SetEnvironmentVariable(name, value);
    }

    private static string BuildSyncConfigJson(
        string authMode,
        bool includePreviewQuery,
        string successFactorsUsernameEnvName,
        string successFactorsPasswordEnvName,
        string successFactorsClientIdEnvName,
        string successFactorsClientSecretEnvName,
        string adServerEnvName,
        string adUsernameEnvName,
        string adPasswordEnvName)
    {
        var authBlock = string.Equals(authMode, "oauth", StringComparison.OrdinalIgnoreCase)
            ? $$"""
              "auth": {
                "mode": "oauth",
                "oauth": {
                  "tokenUrl": "https://login.successfactors.example.com/oauth/token",
                  "clientId": "literal-client-id",
                  "clientSecret": "literal-client-secret",
                  "companyId": "example-company"
                }
              },
              """
            : $$"""
              "auth": {
                "mode": "basic",
                "basic": {
                  "username": "literal-basic-user",
                  "password": "literal-basic-password"
                }
              },
              """;

        var previewQueryBlock = includePreviewQuery
            ? """
              ,
                "previewQuery": {
                  "entitySet": "PerPerson",
                  "identityField": "personIdExternal",
                  "deltaField": "lastModifiedDateTime",
                  "deltaSyncEnabled": false,
                  "deltaOverlapMinutes": 5,
                  "inactiveStatusField": "emplStatus",
                  "inactiveStatusValues": [ "T" ],
                  "inactiveDateField": "endDate",
                  "pageSize": 25,
                  "select": [ "personIdExternal", "personalInfoNav/firstName" ],
                  "expand": [ "personalInfoNav" ]
                }
              """
            : string.Empty;

        return $$"""
        {
          "secrets": {
            "successFactorsUsernameEnv": "{{successFactorsUsernameEnvName}}",
            "successFactorsPasswordEnv": "{{successFactorsPasswordEnvName}}",
            "successFactorsClientIdEnv": "{{successFactorsClientIdEnvName}}",
            "successFactorsClientSecretEnv": "{{successFactorsClientSecretEnvName}}",
            "adServerEnv": "{{adServerEnvName}}",
            "adUsernameEnv": "{{adUsernameEnvName}}",
            "adBindPasswordEnv": "{{adPasswordEnvName}}"
          },
          "successFactors": {
            "baseUrl": "https://api12.successfactors.com/odata/v2",
            {{authBlock}}
            "query": {
              "entitySet": "EmpJob",
              "identityField": "userId",
              "deltaField": "lastModifiedDateTime",
              "deltaSyncEnabled": true,
              "deltaOverlapMinutes": 10,
              "baseFilter": "emplStatus in 'A','U'",
              "orderBy": "userId asc",
              "inactiveRetentionDays": 180,
              "inactiveStatusField": "emplStatus",
              "inactiveStatusValues": [ "T" ],
              "inactiveDateField": "endDate",
              "pageSize": 200,
              "select": [ "userId", "emplStatus", "startDate" ],
              "expand": [ "companyNav", "departmentNav" ]
            }{{previewQueryBlock}}
          },
          "ad": {
            "server": "literal-ad-server.example.com",
            "port": 636,
            "username": "literal-ad-user@example.com",
            "bindPassword": "literal-ad-password",
            "identityAttribute": "employeeID",
            "defaultActiveOu": "OU=Employees,DC=example,DC=com",
            "prehireOu": "OU=Prehire,DC=example,DC=com",
            "graveyardOu": "OU=Graveyard,DC=example,DC=com",
            "leaveOu": "OU=Leave,DC=example,DC=com",
            "upnSuffix": "example.com",
            "transport": {
              "mode": "ldaps",
              "allowLdapFallback": false,
              "requireCertificateValidation": true,
              "requireSigning": true,
              "trustedCertificateThumbprints": [ "ABC123", "DEF456" ]
            },
            "identityPolicy": {
              "resolveCreateConflictingUpnAndMail": true
            }
          },
          "sync": {
            "enableBeforeStartDays": 7,
            "deletionRetentionDays": 90,
            "maxDegreeOfParallelism": 3,
            "autoDeleteFromGraveyard": true,
            "leaveStatusValues": [ "L", "P" ]
          },
          "safety": {
            "maxCreatesPerRun": 25,
            "maxDisablesPerRun": 10,
            "maxDeletionsPerRun": 5
          },
          "alerts": {
            "enabled": true,
            "subjectPrefix": "[SyncFactors]",
            "graveyardRetentionReport": {
              "enabled": true,
              "intervalDays": 7
            },
            "smtp": {
              "host": "mail.example.com",
              "port": 25,
              "useSsl": false,
              "from": "syncfactors@example.com",
              "to": [ "ops@example.com", "it@example.com" ]
            }
          },
          "reporting": {
            "outputDirectory": "./reports/output"
          }
        }
        """;
    }

    private static string BuildMappingConfigJson()
    {
        return """
        {
          "mappings": [
            {
              "source": "personIdExternal",
              "target": "employeeID",
              "enabled": true,
              "required": true,
              "transform": "Trim"
            },
            {
              "source": "emailNav[0].emailAddress",
              "target": "mail",
              "enabled": true,
              "required": false,
              "transform": "Lower"
            },
            {
              "source": "employmentNav[0].jobInfoNav[0].department",
              "target": "department",
              "enabled": false,
              "required": false,
              "transform": "Trim"
            }
          ]
        }
        """;
    }

    private sealed class StubWebHostEnvironment : IWebHostEnvironment
    {
        public string ApplicationName { get; set; } = "SyncFactors.Api.Tests";

        public IFileProvider WebRootFileProvider { get; set; } = new NullFileProvider();

        public string WebRootPath { get; set; } = string.Empty;

        public string EnvironmentName { get; set; } = "Production";

        public string ContentRootPath { get; set; } = string.Empty;

        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}

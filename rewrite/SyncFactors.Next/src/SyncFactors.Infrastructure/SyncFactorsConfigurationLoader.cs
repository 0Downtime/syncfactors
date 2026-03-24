using System.Text.Json;

namespace SyncFactors.Infrastructure;

public sealed class SyncFactorsConfigurationLoader
{
    private readonly SyncFactorsConfigPathResolver _pathResolver;
    private readonly Lazy<SyncFactorsConfigDocument> _syncConfig;
    private readonly Lazy<MappingConfigDocument> _mappingConfig;

    public SyncFactorsConfigurationLoader(SyncFactorsConfigPathResolver pathResolver)
    {
        _pathResolver = pathResolver;
        _syncConfig = new Lazy<SyncFactorsConfigDocument>(LoadSyncConfig);
        _mappingConfig = new Lazy<MappingConfigDocument>(LoadMappingConfig);
    }

    public SyncFactorsConfigDocument GetSyncConfig() => _syncConfig.Value;

    public MappingConfigDocument GetMappingConfig() => _mappingConfig.Value;

    private SyncFactorsConfigDocument LoadSyncConfig()
    {
        var path = _pathResolver.ResolveConfigPath();
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new InvalidOperationException("SyncFactors config path could not be resolved.");
        }

        var json = File.ReadAllText(path);
        var document = JsonDocument.Parse(json).RootElement;

        return new SyncFactorsConfigDocument(
            SuccessFactors: new SuccessFactorsConfig(
                BaseUrl: document.GetRequiredObject("successFactors").GetRequiredString("baseUrl"),
                Auth: LoadSuccessFactorsAuth(document.GetRequiredObject("successFactors").GetRequiredObject("auth")),
                Query: new SuccessFactorsQueryConfig(
                    EntitySet: document.GetRequiredObject("successFactors").GetRequiredObject("query").GetRequiredString("entitySet"),
                    IdentityField: document.GetRequiredObject("successFactors").GetRequiredObject("query").GetRequiredString("identityField"),
                    DeltaField: document.GetRequiredObject("successFactors").GetRequiredObject("query").GetRequiredString("deltaField"),
                    Select: document.GetRequiredObject("successFactors").GetRequiredObject("query").GetRequiredStringArray("select"),
                    Expand: document.GetRequiredObject("successFactors").GetRequiredObject("query").GetRequiredStringArray("expand")),
                PreviewQuery: document.GetRequiredObject("successFactors").TryGetObject("previewQuery", out var previewQuery)
                    ? new SuccessFactorsQueryConfig(
                        EntitySet: previewQuery.GetRequiredString("entitySet"),
                        IdentityField: previewQuery.GetRequiredString("identityField"),
                        DeltaField: previewQuery.TryGetString("deltaField") ?? string.Empty,
                        Select: previewQuery.GetRequiredStringArray("select"),
                        Expand: previewQuery.TryGetStringArray("expand") ?? [])
                    : null),
            Ad: new ActiveDirectoryConfig(
                Server: document.GetRequiredObject("ad").GetRequiredString("server"),
                Username: document.GetRequiredObject("ad").TryGetString("username"),
                BindPassword: document.GetRequiredObject("ad").TryGetString("bindPassword"),
                IdentityAttribute: document.GetRequiredObject("ad").GetRequiredString("identityAttribute"),
                DefaultActiveOu: document.GetRequiredObject("ad").GetRequiredString("defaultActiveOu"),
                GraveyardOu: document.GetRequiredObject("ad").GetRequiredString("graveyardOu")),
            Sync: new SyncPolicyConfig(
                EnableBeforeStartDays: document.GetRequiredObject("sync").GetRequiredInt32("enableBeforeStartDays"),
                DeletionRetentionDays: document.GetRequiredObject("sync").GetRequiredInt32("deletionRetentionDays")),
            Safety: new SafetyConfig(
                MaxCreatesPerRun: document.GetRequiredObject("safety").GetRequiredInt32("maxCreatesPerRun"),
                MaxDisablesPerRun: document.GetRequiredObject("safety").GetRequiredInt32("maxDisablesPerRun"),
                MaxDeletionsPerRun: document.GetRequiredObject("safety").GetRequiredInt32("maxDeletionsPerRun")),
            Reporting: new ReportingConfig(
                OutputDirectory: document.GetRequiredObject("reporting").GetRequiredString("outputDirectory")));
    }

    private MappingConfigDocument LoadMappingConfig()
    {
        var path = _pathResolver.ResolveMappingConfigPath();
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new InvalidOperationException("SyncFactors mapping config path could not be resolved.");
        }

        var json = File.ReadAllText(path);
        var document = JsonDocument.Parse(json).RootElement;
        var mappings = document.GetRequiredArray("mappings")
            .Select(item => new AttributeMappingConfig(
                Source: item.GetRequiredString("source"),
                Target: item.GetRequiredString("target"),
                Enabled: item.GetRequiredBoolean("enabled"),
                Required: item.GetRequiredBoolean("required"),
                Transform: item.GetRequiredString("transform")))
            .ToArray();

        return new MappingConfigDocument(mappings);
    }

    private static SuccessFactorsAuthConfig LoadSuccessFactorsAuth(JsonElement auth)
    {
        var mode = auth.GetRequiredString("mode");
        return new SuccessFactorsAuthConfig(
            Mode: mode,
            Basic: auth.TryGetObject("basic", out var basic)
                ? new SuccessFactorsBasicAuthConfig(
                    Username: basic.TryGetString("username") ?? string.Empty,
                    Password: basic.TryGetString("password") ?? string.Empty)
                : null,
            OAuth: auth.TryGetObject("oauth", out var oauth)
                ? new SuccessFactorsOAuthConfig(
                    TokenUrl: oauth.GetRequiredString("tokenUrl"),
                    ClientId: oauth.GetRequiredString("clientId"),
                    ClientSecret: oauth.GetRequiredString("clientSecret"),
                    CompanyId: oauth.TryGetString("companyId"))
                : null);
    }
}

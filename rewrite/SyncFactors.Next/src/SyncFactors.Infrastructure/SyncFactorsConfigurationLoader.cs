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
        var secrets = LoadSecrets(document);

        return new SyncFactorsConfigDocument(
            Secrets: secrets,
            SuccessFactors: new SuccessFactorsConfig(
                BaseUrl: document.GetRequiredObject("successFactors").GetRequiredString("baseUrl"),
                Auth: LoadSuccessFactorsAuth(document.GetRequiredObject("successFactors").GetRequiredObject("auth"), secrets),
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
                Server: GetRequiredEnvironmentValue(secrets.AdServerEnv, "secrets.adServerEnv"),
                Username: GetOptionalEnvironmentValue(secrets.AdUsernameEnv),
                BindPassword: GetOptionalEnvironmentValue(secrets.AdBindPasswordEnv),
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

    private static SecretsConfig LoadSecrets(JsonElement document)
    {
        var secrets = document.GetRequiredObject("secrets");
        return new SecretsConfig(
            SuccessFactorsUsernameEnv: secrets.TryGetString("successFactorsUsernameEnv"),
            SuccessFactorsPasswordEnv: secrets.TryGetString("successFactorsPasswordEnv"),
            SuccessFactorsClientIdEnv: secrets.TryGetString("successFactorsClientIdEnv"),
            SuccessFactorsClientSecretEnv: secrets.TryGetString("successFactorsClientSecretEnv"),
            AdServerEnv: secrets.TryGetString("adServerEnv"),
            AdUsernameEnv: secrets.TryGetString("adUsernameEnv"),
            AdBindPasswordEnv: secrets.TryGetString("adBindPasswordEnv"));
    }

    private static SuccessFactorsAuthConfig LoadSuccessFactorsAuth(JsonElement auth, SecretsConfig secrets)
    {
        var mode = auth.GetRequiredString("mode");
        return new SuccessFactorsAuthConfig(
            Mode: mode,
            Basic: auth.TryGetObject("basic", out var basic) && string.Equals(mode, "basic", StringComparison.OrdinalIgnoreCase)
                ? new SuccessFactorsBasicAuthConfig(
                    Username: GetRequiredEnvironmentValue(secrets.SuccessFactorsUsernameEnv, "secrets.successFactorsUsernameEnv"),
                    Password: GetRequiredEnvironmentValue(secrets.SuccessFactorsPasswordEnv, "secrets.successFactorsPasswordEnv"))
                : null,
            OAuth: auth.TryGetObject("oauth", out var oauth) && string.Equals(mode, "oauth", StringComparison.OrdinalIgnoreCase)
                ? new SuccessFactorsOAuthConfig(
                    TokenUrl: oauth.GetRequiredString("tokenUrl"),
                    ClientId: GetRequiredEnvironmentValue(secrets.SuccessFactorsClientIdEnv, "secrets.successFactorsClientIdEnv"),
                    ClientSecret: GetRequiredEnvironmentValue(secrets.SuccessFactorsClientSecretEnv, "secrets.successFactorsClientSecretEnv"),
                    CompanyId: oauth.TryGetString("companyId"))
                : null);
    }

    private static string GetRequiredEnvironmentValue(string? environmentVariableName, string configPath)
    {
        if (string.IsNullOrWhiteSpace(environmentVariableName))
        {
            throw new InvalidOperationException($"Required environment variable mapping '{configPath}' was not configured.");
        }

        var value = Environment.GetEnvironmentVariable(environmentVariableName);
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new InvalidOperationException($"Environment variable '{environmentVariableName}' was not set.");
        }

        return value;
    }

    private static string? GetOptionalEnvironmentValue(string? environmentVariableName)
    {
        if (string.IsNullOrWhiteSpace(environmentVariableName))
        {
            return null;
        }

        var value = Environment.GetEnvironmentVariable(environmentVariableName);
        return string.IsNullOrWhiteSpace(value) ? null : value;
    }
}

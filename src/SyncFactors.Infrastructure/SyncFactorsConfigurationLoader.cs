using System.Text.Json;
using SyncFactors.Domain;

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

    public string GetResolvedSyncConfigPath()
    {
        var path = _pathResolver.ResolveConfigPath();
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new InvalidOperationException("SyncFactors config path could not be resolved.");
        }

        return path;
    }

    private SyncFactorsConfigDocument LoadSyncConfig()
    {
        var path = GetResolvedSyncConfigPath();
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
                    DeltaSyncEnabled: document.GetRequiredObject("successFactors").GetRequiredObject("query").TryGetBoolean("deltaSyncEnabled") ?? false,
                    DeltaOverlapMinutes: TryGetInt32(document.GetRequiredObject("successFactors").GetRequiredObject("query"), "deltaOverlapMinutes") ?? 5,
                    BaseFilter: document.GetRequiredObject("successFactors").GetRequiredObject("query").TryGetString("baseFilter"),
                    OrderBy: document.GetRequiredObject("successFactors").GetRequiredObject("query").TryGetString("orderBy"),
                    InactiveRetentionDays: LoadInactiveRetentionDays(document.GetRequiredObject("successFactors").GetRequiredObject("query")),
                    InactiveStatusField: document.GetRequiredObject("successFactors").GetRequiredObject("query").TryGetString("inactiveStatusField") ?? "emplStatus",
                    InactiveStatusValues: document.GetRequiredObject("successFactors").GetRequiredObject("query").TryGetStringArray("inactiveStatusValues") ?? ["T"],
                    InactiveDateField: document.GetRequiredObject("successFactors").GetRequiredObject("query").TryGetString("inactiveDateField") ?? "endDate",
                    AsOfDate: document.GetRequiredObject("successFactors").GetRequiredObject("query").TryGetString("asOfDate"),
                    PageSize: TryGetInt32(document.GetRequiredObject("successFactors").GetRequiredObject("query"), "pageSize") ?? 200,
                    Select: document.GetRequiredObject("successFactors").GetRequiredObject("query").GetRequiredStringArray("select"),
                    Expand: document.GetRequiredObject("successFactors").GetRequiredObject("query").GetRequiredStringArray("expand")),
                PreviewQuery: document.GetRequiredObject("successFactors").TryGetObject("previewQuery", out var previewQuery)
                    ? new SuccessFactorsQueryConfig(
                        EntitySet: previewQuery.GetRequiredString("entitySet"),
                        IdentityField: previewQuery.GetRequiredString("identityField"),
                        DeltaField: previewQuery.TryGetString("deltaField") ?? string.Empty,
                        DeltaSyncEnabled: previewQuery.TryGetBoolean("deltaSyncEnabled") ?? false,
                        DeltaOverlapMinutes: TryGetInt32(previewQuery, "deltaOverlapMinutes") ?? 5,
                        BaseFilter: previewQuery.TryGetString("baseFilter"),
                        OrderBy: previewQuery.TryGetString("orderBy"),
                        InactiveRetentionDays: LoadInactiveRetentionDays(previewQuery),
                        InactiveStatusField: previewQuery.TryGetString("inactiveStatusField") ?? "emplStatus",
                        InactiveStatusValues: previewQuery.TryGetStringArray("inactiveStatusValues") ?? ["T"],
                        InactiveDateField: previewQuery.TryGetString("inactiveDateField") ?? "endDate",
                        AsOfDate: previewQuery.TryGetString("asOfDate"),
                        PageSize: TryGetInt32(previewQuery, "pageSize") ?? 200,
                        Select: previewQuery.GetRequiredStringArray("select"),
                        Expand: previewQuery.TryGetStringArray("expand") ?? [])
                    : null),
            Ad: new ActiveDirectoryConfig(
                Server: GetRequiredSecretValue(
                    environmentVariableName: secrets.AdServerEnv,
                    fallbackJsonValue: document.GetRequiredObject("ad").TryGetString("server"),
                    configPath: "secrets.adServerEnv",
                    secretLabel: "AD server"),
                Port: TryGetInt32(document.GetRequiredObject("ad"), "port"),
                Username: GetOptionalSecretValue(
                    environmentVariableName: secrets.AdUsernameEnv,
                    fallbackJsonValue: document.GetRequiredObject("ad").TryGetString("username")),
                BindPassword: GetOptionalSecretValue(
                    environmentVariableName: secrets.AdBindPasswordEnv,
                    fallbackJsonValue: document.GetRequiredObject("ad").TryGetString("bindPassword")),
                IdentityAttribute: NormalizeRequiredValue(
                    document.GetRequiredObject("ad").GetRequiredString("identityAttribute"),
                    "ad.identityAttribute"),
                DefaultActiveOu: document.GetRequiredObject("ad").GetRequiredString("defaultActiveOu"),
                PrehireOu: document.GetRequiredObject("ad").GetRequiredString("prehireOu"),
                GraveyardOu: document.GetRequiredObject("ad").GetRequiredString("graveyardOu"),
                Transport: LoadActiveDirectoryTransport(document.GetRequiredObject("ad")),
                IdentityPolicy: LoadActiveDirectoryIdentityPolicy(document.GetRequiredObject("ad")),
                LeaveOu: document.GetRequiredObject("ad").TryGetString("leaveOu"),
                UpnSuffix: DirectoryIdentityFormatter.NormalizeEmailDomain(document.GetRequiredObject("ad").TryGetString("upnSuffix")),
                LicensingGroups: document.GetRequiredObject("ad").TryGetStringArray("licensingGroups")?
                    .Select(item => NormalizeRequiredValue(item, "ad.licensingGroups[]"))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToArray() ?? [],
                IdentityCorrelation: LoadActiveDirectoryIdentityCorrelation(document.GetRequiredObject("ad"))),
            Sync: new SyncPolicyConfig(
                EnableBeforeStartDays: document.GetRequiredObject("sync").GetRequiredInt32("enableBeforeStartDays"),
                DeletionRetentionDays: document.GetRequiredObject("sync").GetRequiredInt32("deletionRetentionDays"),
                MaxDegreeOfParallelism: TryGetInt32(document.GetRequiredObject("sync"), "maxDegreeOfParallelism") ?? 2,
                RealSyncEnabled: document.GetRequiredObject("sync").TryGetBoolean("realSyncEnabled") ?? true,
                AutoDeleteFromGraveyard: document.GetRequiredObject("sync").TryGetBoolean("autoDeleteFromGraveyard") ?? false,
                LeaveStatusValues: document.GetRequiredObject("sync").TryGetStringArray("leaveStatusValues") ?? []),
            Safety: new SafetyConfig(
                MaxCreatesPerRun: document.GetRequiredObject("safety").GetRequiredInt32("maxCreatesPerRun"),
                MaxDisablesPerRun: document.GetRequiredObject("safety").GetRequiredInt32("maxDisablesPerRun"),
                MaxDeletionsPerRun: document.GetRequiredObject("safety").GetRequiredInt32("maxDeletionsPerRun")),
            Alerts: LoadAlerts(document),
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
                Source: NormalizeRequiredValue(item.GetRequiredString("source"), "mappings[].source"),
                Target: NormalizeRequiredValue(item.GetRequiredString("target"), "mappings[].target"),
                Enabled: item.GetRequiredBoolean("enabled"),
                Required: item.GetRequiredBoolean("required"),
                Transform: NormalizeRequiredValue(item.GetRequiredString("transform"), "mappings[].transform")))
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
                    Username: GetRequiredSecretValue(
                        environmentVariableName: secrets.SuccessFactorsUsernameEnv,
                        fallbackJsonValue: basic.TryGetString("username"),
                        configPath: "secrets.successFactorsUsernameEnv",
                        secretLabel: "SuccessFactors basic username"),
                    Password: GetRequiredSecretValue(
                        environmentVariableName: secrets.SuccessFactorsPasswordEnv,
                        fallbackJsonValue: basic.TryGetString("password"),
                        configPath: "secrets.successFactorsPasswordEnv",
                        secretLabel: "SuccessFactors basic password"))
                : null,
            OAuth: auth.TryGetObject("oauth", out var oauth) && string.Equals(mode, "oauth", StringComparison.OrdinalIgnoreCase)
                ? new SuccessFactorsOAuthConfig(
                    TokenUrl: oauth.GetRequiredString("tokenUrl"),
                    ClientId: GetRequiredSecretValue(
                        environmentVariableName: secrets.SuccessFactorsClientIdEnv,
                        fallbackJsonValue: oauth.TryGetString("clientId"),
                        configPath: "secrets.successFactorsClientIdEnv",
                        secretLabel: "SuccessFactors OAuth client ID"),
                    ClientSecret: GetRequiredSecretValue(
                        environmentVariableName: secrets.SuccessFactorsClientSecretEnv,
                        fallbackJsonValue: oauth.TryGetString("clientSecret"),
                        configPath: "secrets.successFactorsClientSecretEnv",
                        secretLabel: "SuccessFactors OAuth client secret"),
                    CompanyId: oauth.TryGetString("companyId"))
                : null);
    }

    private static string GetRequiredSecretValue(
        string? environmentVariableName,
        string? fallbackJsonValue,
        string configPath,
        string secretLabel)
    {
        var environmentValue = GetOptionalEnvironmentValue(environmentVariableName);
        if (!string.IsNullOrWhiteSpace(environmentValue))
        {
            return environmentValue;
        }

        if (!string.IsNullOrWhiteSpace(fallbackJsonValue))
        {
            return fallbackJsonValue;
        }

        if (!string.IsNullOrWhiteSpace(environmentVariableName))
        {
            throw new InvalidOperationException($"Environment variable '{environmentVariableName}' was not set for {secretLabel}.");
        }

        throw new InvalidOperationException($"Required secret '{secretLabel}' was not configured in '{configPath}' or as a literal JSON value.");
    }

    private static string? GetOptionalSecretValue(string? environmentVariableName, string? fallbackJsonValue)
    {
        var environmentValue = GetOptionalEnvironmentValue(environmentVariableName);
        return !string.IsNullOrWhiteSpace(environmentValue) ? environmentValue : fallbackJsonValue;
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

    private static string NormalizeRequiredValue(string value, string configPath)
    {
        var normalized = value.Trim();
        if (string.IsNullOrWhiteSpace(normalized))
        {
            throw new InvalidOperationException($"Property '{configPath}' must not be empty.");
        }

        return normalized;
    }

    private static AlertingConfig LoadAlerts(JsonElement document)
    {
        if (!document.TryGetObject("alerts", out var alerts))
        {
            return new AlertingConfig(
                Enabled: false,
                SubjectPrefix: "[SyncFactors]",
                GraveyardRetentionReport: new GraveyardRetentionReportConfig(Enabled: false, IntervalDays: 7),
                Smtp: null);
        }

        var smtp = alerts.TryGetObject("smtp", out var smtpElement)
            ? new SmtpConfig(
                Host: smtpElement.GetRequiredString("host"),
                Port: TryGetInt32(smtpElement, "port") ?? 25,
                UseSsl: smtpElement.TryGetBoolean("useSsl") ?? false,
                From: smtpElement.GetRequiredString("from"),
                To: smtpElement.GetRequiredStringArray("to"))
            : null;

        var report = alerts.TryGetObject("graveyardRetentionReport", out var reportElement)
            ? new GraveyardRetentionReportConfig(
                Enabled: reportElement.TryGetBoolean("enabled") ?? false,
                IntervalDays: TryGetInt32(reportElement, "intervalDays") ?? 7)
            : new GraveyardRetentionReportConfig(Enabled: false, IntervalDays: 7);

        return new AlertingConfig(
            Enabled: alerts.TryGetBoolean("enabled") ?? false,
            SubjectPrefix: alerts.TryGetString("subjectPrefix") ?? "[SyncFactors]",
            GraveyardRetentionReport: report,
            Smtp: smtp);
    }

    private static int? TryGetInt32(JsonElement element, string propertyName)
    {
        return element.TryGetProperty(propertyName, out var property) &&
               property.ValueKind == JsonValueKind.Number &&
               property.TryGetInt32(out var value)
            ? value
            : null;
    }

    private static int? LoadInactiveRetentionDays(JsonElement query)
    {
        var days = TryGetInt32(query, "inactiveRetentionDays");
        if (days is null)
        {
            return null;
        }

        if (days.Value < 1)
        {
            throw new InvalidOperationException("Property 'inactiveRetentionDays' must be a positive integer when configured.");
        }

        return days;
    }

    private static ActiveDirectoryIdentityPolicyConfig LoadActiveDirectoryIdentityPolicy(JsonElement ad)
    {
        if (!ad.TryGetObject("identityPolicy", out var identityPolicy))
        {
            return new ActiveDirectoryIdentityPolicyConfig(ResolveCreateConflictingUpnAndMail: true);
        }

        return new ActiveDirectoryIdentityPolicyConfig(
            ResolveCreateConflictingUpnAndMail: identityPolicy.TryGetBoolean("resolveCreateConflictingUpnAndMail") ?? true);
    }

    private static ActiveDirectoryIdentityCorrelationConfig LoadActiveDirectoryIdentityCorrelation(JsonElement ad)
    {
        if (!ad.TryGetObject("identityCorrelation", out var identityCorrelation))
        {
            return new ActiveDirectoryIdentityCorrelationConfig(
                Enabled: false,
                SuccessorPersonIdExternalAttribute: null,
                PreviousPersonIdExternalAttribute: null);
        }

        return new ActiveDirectoryIdentityCorrelationConfig(
            Enabled: identityCorrelation.TryGetBoolean("enabled") ?? false,
            SuccessorPersonIdExternalAttribute: identityCorrelation.TryGetString("successorPersonIdExternalAttribute"),
            PreviousPersonIdExternalAttribute: identityCorrelation.TryGetString("previousPersonIdExternalAttribute"));
    }

    private static ActiveDirectoryTransportConfig LoadActiveDirectoryTransport(JsonElement ad)
    {
        if (!ad.TryGetObject("transport", out var transport))
        {
            return new ActiveDirectoryTransportConfig(
                Mode: "ldaps",
                AllowLdapFallback: false,
                RequireCertificateValidation: true,
                RequireSigning: true,
                TrustedCertificateThumbprints: [],
                AllowCreateEnableWithoutPasswordProvisioning: false);
        }

        return new ActiveDirectoryTransportConfig(
            Mode: transport.TryGetString("mode") ?? "ldaps",
            AllowLdapFallback: transport.TryGetBoolean("allowLdapFallback") ?? false,
            RequireCertificateValidation: transport.TryGetBoolean("requireCertificateValidation") ?? true,
            RequireSigning: transport.TryGetBoolean("requireSigning") ?? true,
            TrustedCertificateThumbprints: transport.TryGetStringArray("trustedCertificateThumbprints") ?? [],
            AllowCreateEnableWithoutPasswordProvisioning: transport.TryGetBoolean("allowCreateEnableWithoutPasswordProvisioning") ?? false);
    }
}

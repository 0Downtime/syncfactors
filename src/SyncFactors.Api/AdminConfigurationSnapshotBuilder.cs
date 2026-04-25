using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;
using SyncFactors.Infrastructure;

namespace SyncFactors.Api;

public sealed class AdminConfigurationSnapshotBuilder(
    SyncFactorsConfigurationLoader configLoader,
    SyncFactorsConfigPathResolver configPathResolver,
    ScaffoldDataPathResolver scaffoldDataPathResolver,
    SqlitePathResolver sqlitePathResolver,
    IConfiguration configuration,
    IOptions<LocalAuthOptions> authOptions,
    IWebHostEnvironment hostEnvironment)
{
    private const string SyncConfigSource = "Sync config";
    private const string HostConfigSource = "Environment/AppSettings";
    private const string DefaultSource = "Default";
    private const string HiddenValue = "Hidden";
    private const string NotConfiguredValue = "Not configured";

    internal AdminConfigurationPageSnapshot Build()
    {
        var sync = configLoader.GetSyncConfig();
        var mapping = configLoader.GetMappingConfig();
        var auth = authOptions.Value;
        var runProfile = ResolveRunProfile();

        return new AdminConfigurationPageSnapshot(
            [
                BuildDeploymentSection(runProfile),
                BuildAuthenticationSection(auth),
                BuildSuccessFactorsSection(sync),
                BuildActiveDirectorySection(sync),
                BuildOperationsSection(sync),
                BuildMappingsSection(mapping)
            ]);
    }

    private AdminConfigurationSectionViewModel BuildDeploymentSection(string runProfile)
    {
        var syncConfigPath = configLoader.GetResolvedSyncConfigPath();
        var mappingConfigPath = configPathResolver.ResolveMappingConfigPath();
        var sqlitePath = sqlitePathResolver.ResolveConfiguredPath();
        var scaffoldDataPath = scaffoldDataPathResolver.Resolve();
        var realtimeEnabled = configuration.GetValue<bool?>("SyncFactors:Realtime:Enabled") ?? true;
        var dashboardHealthProbesEnabled = configuration.GetValue<bool?>("SyncFactors:Dashboard:HealthProbes:Enabled") ?? true;
        var cspEnabled = configuration.GetValue<bool?>("SyncFactors:SecurityHeaders:EnableContentSecurityPolicy")
            ?? !hostEnvironment.IsDevelopment();
        var applicationInsightsConfigured = IsApplicationInsightsConfigured();

        return new AdminConfigurationSectionViewModel(
            Id: "deployment",
            Eyebrow: "Runtime",
            Title: "Deployment",
            Description: "Resolved host settings, profile selection, and runtime paths for the current portal instance.",
            Groups:
            [
                new AdminConfigurationGroupViewModel(
                    Title: "Current Environment",
                    Entries:
                    [
                        CreateEntry(
                            "Run profile",
                            runProfile,
                            GetHostSource("SYNCFACTORS_RUN_PROFILE")),
                        CreateEntry(
                            "Sync config path",
                            FormatResolvedPathValue(
                                configuration["SyncFactors:ConfigPath"],
                                $"profile '{runProfile}' default",
                                syncConfigPath),
                            GetHostSource("SyncFactors:ConfigPath", "SYNCFACTORS_RUN_PROFILE")),
                        CreateEntry(
                            "Mapping config path",
                            FormatResolvedPathValue(
                                configuration["SyncFactors:MappingConfigPath"],
                                "config/local.syncfactors.mapping-config.json",
                                mappingConfigPath),
                            GetHostSource("SyncFactors:MappingConfigPath")),
                        CreateEntry(
                            "SQLite path",
                            FormatResolvedPathValue(
                                configuration["SyncFactors:SqlitePath"],
                                "state/runtime/syncfactors.db",
                                sqlitePath),
                            GetHostSource("SyncFactors:SqlitePath")),
                        CreateEntry(
                            "Scaffold data path",
                            FormatResolvedPathValue(
                                configuration["SyncFactors:ScaffoldDataPath"],
                                "config/scaffold-data.json",
                                scaffoldDataPath),
                            GetHostSource("SyncFactors:ScaffoldDataPath")),
                        CreateEntry(
                            "Realtime updates",
                            FormatEnabledDisabled(realtimeEnabled),
                            GetHostSource("SyncFactors:Realtime:Enabled")),
                        CreateEntry(
                            "Dashboard health probes default",
                            FormatEnabledDisabled(dashboardHealthProbesEnabled),
                            GetHostSource("SyncFactors:Dashboard:HealthProbes:Enabled")),
                        CreateEntry(
                            "Dashboard health probe frequency default",
                            $"{DashboardSettingsProvider.ClampHealthProbeIntervalSeconds(configuration.GetValue<int?>("SyncFactors:Dashboard:HealthProbes:IntervalSeconds") ?? 45)} seconds",
                            GetHostSource("SyncFactors:Dashboard:HealthProbes:IntervalSeconds")),
                        CreateEntry(
                            "Content Security Policy",
                            FormatEnabledDisabled(cspEnabled),
                            GetHostSource("SyncFactors:SecurityHeaders:EnableContentSecurityPolicy")),
                        CreateEntry(
                            "Application Insights",
                            FormatEnabledDisabled(applicationInsightsConfigured),
                            applicationInsightsConfigured ? HostConfigSource : DefaultSource)
                    ])
            ]);
    }

    private AdminConfigurationSectionViewModel BuildAuthenticationSection(LocalAuthOptions auth)
    {
        return new AdminConfigurationSectionViewModel(
            Id: "authentication",
            Eyebrow: "Access",
            Title: "Authentication",
            Description: "Portal sign-in mode, session behavior, and any configured OIDC integration values.",
            Groups:
            [
                new AdminConfigurationGroupViewModel(
                    Title: "Session",
                    Entries:
                    [
                        CreateEntry(
                            "Authentication mode",
                            FormatAuthenticationMode(auth.Mode),
                            GetHostSource("SyncFactors:Auth:Mode")),
                        CreateEntry(
                            "Local break-glass access",
                            FormatEnabledDisabled(IsLocalAuthenticationEnabled(auth)),
                            GetHostSource("SyncFactors:Auth:Mode", "SyncFactors:Auth:LocalBreakGlass:Enabled")),
                        CreateEntry(
                            "Absolute session lifetime",
                            FormatHours(auth.AbsoluteSessionHours),
                            GetHostSource("SyncFactors:Auth:AbsoluteSessionHours")),
                        CreateEntry(
                            "Idle timeout",
                            FormatMinutes(auth.IdleTimeoutMinutes),
                            GetHostSource("SyncFactors:Auth:IdleTimeoutMinutes")),
                        CreateEntry(
                            "Remember me",
                            FormatEnabledDisabled(auth.AllowRememberMe),
                            GetHostSource("SyncFactors:Auth:AllowRememberMe")),
                        CreateEntry(
                            "Remember-me session lifetime",
                            FormatHours(auth.RememberMeSessionHours),
                            GetHostSource("SyncFactors:Auth:RememberMeSessionHours")),
                        CreateEntry(
                            "Bootstrap admin username",
                            FormatOptional(auth.BootstrapAdmin.Username),
                            GetHostSource("SyncFactors:Auth:BootstrapAdmin:Username")),
                        CreateEntry(
                            "Bootstrap admin password",
                            FormatHiddenOrMissing(auth.BootstrapAdmin.Password),
                            GetHostSource("SyncFactors:Auth:BootstrapAdmin:Password"),
                            isSecretOmitted: !string.IsNullOrWhiteSpace(auth.BootstrapAdmin.Password))
                    ]),
                new AdminConfigurationGroupViewModel(
                    Title: "OIDC",
                    Entries:
                    [
                        CreateEntry(
                            "Authority",
                            FormatOptional(auth.Oidc.Authority),
                            GetHostSource("SyncFactors:Auth:Oidc:Authority")),
                        CreateEntry(
                            "Client ID",
                            FormatOptional(auth.Oidc.ClientId),
                            GetHostSource("SyncFactors:Auth:Oidc:ClientId")),
                        CreateEntry(
                            "Client secret",
                            FormatHiddenOrMissing(auth.Oidc.ClientSecret),
                            GetHostSource("SyncFactors:Auth:Oidc:ClientSecret"),
                            isSecretOmitted: !string.IsNullOrWhiteSpace(auth.Oidc.ClientSecret)),
                        CreateEntry(
                            "Callback path",
                            FormatOptional(auth.Oidc.CallbackPath),
                            GetHostSource("SyncFactors:Auth:Oidc:CallbackPath")),
                        CreateEntry(
                            "Signed-out callback path",
                            FormatOptional(auth.Oidc.SignedOutCallbackPath),
                            GetHostSource("SyncFactors:Auth:Oidc:SignedOutCallbackPath")),
                        CreateEntry(
                            "Roles claim type",
                            FormatOptional(auth.Oidc.RolesClaimType),
                            GetHostSource("SyncFactors:Auth:Oidc:RolesClaimType")),
                        CreateEntry(
                            "Display name claim type",
                            FormatOptional(auth.Oidc.DisplayNameClaimType),
                            GetHostSource("SyncFactors:Auth:Oidc:DisplayNameClaimType")),
                        CreateEntry(
                            "Username claim type",
                            FormatOptional(auth.Oidc.UsernameClaimType),
                            GetHostSource("SyncFactors:Auth:Oidc:UsernameClaimType")),
                        CreateEntry(
                            "Viewer groups",
                            FormatCollection(auth.Oidc.ViewerGroups),
                            GetHostSource("SyncFactors:Auth:Oidc:ViewerGroups")),
                        CreateEntry(
                            "Operator groups",
                            FormatCollection(auth.Oidc.OperatorGroups),
                            GetHostSource("SyncFactors:Auth:Oidc:OperatorGroups")),
                        CreateEntry(
                            "Admin groups",
                            FormatCollection(auth.Oidc.AdminGroups),
                            GetHostSource("SyncFactors:Auth:Oidc:AdminGroups"))
                    ])
            ]);
    }

    private AdminConfigurationSectionViewModel BuildSuccessFactorsSection(SyncFactorsConfigDocument sync)
    {
        var authMode = sync.SuccessFactors.Auth.Mode?.Trim().ToLowerInvariant();
        var groups = new List<AdminConfigurationGroupViewModel>
        {
            new(
                Title: "Connection",
                Entries:
                [
                    CreateEntry("Base URL", sync.SuccessFactors.BaseUrl, SyncConfigSource),
                    CreateEntry("Auth mode", authMode ?? NotConfiguredValue, SyncConfigSource)
                ])
        };

        if (string.Equals(authMode, "basic", StringComparison.OrdinalIgnoreCase))
        {
            groups.Add(new AdminConfigurationGroupViewModel(
                Title: "Basic Auth",
                Entries:
                [
                    CreateEntry(
                        "Username",
                        FormatHiddenOrMissing(sync.SuccessFactors.Auth.Basic?.Username),
                        ResolveSecretSource(sync.Secrets.SuccessFactorsUsernameEnv, SyncConfigSource),
                        isSecretOmitted: !string.IsNullOrWhiteSpace(sync.SuccessFactors.Auth.Basic?.Username)),
                    CreateEntry(
                        "Password",
                        FormatHiddenOrMissing(sync.SuccessFactors.Auth.Basic?.Password),
                        ResolveSecretSource(sync.Secrets.SuccessFactorsPasswordEnv, SyncConfigSource),
                        isSecretOmitted: !string.IsNullOrWhiteSpace(sync.SuccessFactors.Auth.Basic?.Password))
                ]));
        }

        if (string.Equals(authMode, "oauth", StringComparison.OrdinalIgnoreCase))
        {
            groups.Add(new AdminConfigurationGroupViewModel(
                Title: "OAuth",
                Entries:
                [
                    CreateEntry(
                        "Token URL",
                        FormatOptional(sync.SuccessFactors.Auth.OAuth?.TokenUrl),
                        SyncConfigSource),
                    CreateEntry(
                        "Client ID",
                        FormatOptional(sync.SuccessFactors.Auth.OAuth?.ClientId),
                        ResolveSecretSource(sync.Secrets.SuccessFactorsClientIdEnv, SyncConfigSource)),
                    CreateEntry(
                        "Client secret",
                        FormatHiddenOrMissing(sync.SuccessFactors.Auth.OAuth?.ClientSecret),
                        ResolveSecretSource(sync.Secrets.SuccessFactorsClientSecretEnv, SyncConfigSource),
                        isSecretOmitted: !string.IsNullOrWhiteSpace(sync.SuccessFactors.Auth.OAuth?.ClientSecret)),
                    CreateEntry(
                        "Company ID",
                        FormatOptional(sync.SuccessFactors.Auth.OAuth?.CompanyId),
                        SyncConfigSource)
                ]));
        }

        groups.Add(BuildQueryGroup("Primary query", sync.SuccessFactors.Query));
        groups.Add(sync.SuccessFactors.PreviewQuery is null
            ? new AdminConfigurationGroupViewModel(
                Title: "Preview query",
                Entries:
                [
                    CreateEntry("Preview query", NotConfiguredValue, SyncConfigSource)
                ])
            : BuildQueryGroup("Preview query", sync.SuccessFactors.PreviewQuery));

        return new AdminConfigurationSectionViewModel(
            Id: "successfactors",
            Eyebrow: "Source",
            Title: "SuccessFactors",
            Description: "Effective upstream connection settings and OData query shapes used for worker data retrieval.",
            Groups: groups);
    }

    private AdminConfigurationSectionViewModel BuildActiveDirectorySection(SyncFactorsConfigDocument sync)
    {
        return new AdminConfigurationSectionViewModel(
            Id: "active-directory",
            Eyebrow: "Directory",
            Title: "Active Directory",
            Description: "Directory connection, OU routing targets, and transport/identity rules applied by the worker.",
            Groups:
            [
                new AdminConfigurationGroupViewModel(
                    Title: "Connection",
                    Entries:
                    [
                        CreateEntry(
                            "Server",
                            HasEnvironmentValue(sync.Secrets.AdServerEnv)
                                ? HiddenValue
                                : FormatOptional(sync.Ad.Server),
                            ResolveSecretSource(sync.Secrets.AdServerEnv, SyncConfigSource),
                            isSecretOmitted: HasEnvironmentValue(sync.Secrets.AdServerEnv) && !string.IsNullOrWhiteSpace(sync.Ad.Server)),
                        CreateEntry(
                            "Port",
                            sync.Ad.Port?.ToString() ?? NotConfiguredValue,
                            SyncConfigSource),
                        CreateEntry(
                            "Bind username",
                            HasEnvironmentValue(sync.Secrets.AdUsernameEnv)
                                ? HiddenValue
                                : FormatOptional(sync.Ad.Username),
                            ResolveSecretSource(sync.Secrets.AdUsernameEnv, SyncConfigSource),
                            isSecretOmitted: HasEnvironmentValue(sync.Secrets.AdUsernameEnv) && !string.IsNullOrWhiteSpace(sync.Ad.Username)),
                        CreateEntry(
                            "Bind password",
                            FormatHiddenOrMissing(sync.Ad.BindPassword),
                            ResolveSecretSource(sync.Secrets.AdBindPasswordEnv, SyncConfigSource),
                            isSecretOmitted: !string.IsNullOrWhiteSpace(sync.Ad.BindPassword))
                    ]),
                new AdminConfigurationGroupViewModel(
                    Title: "Managed OUs",
                    Entries:
                    [
                        CreateEntry("Default active OU", sync.Ad.DefaultActiveOu, SyncConfigSource),
                        CreateEntry("Prehire OU", sync.Ad.PrehireOu, SyncConfigSource),
                        CreateEntry("Graveyard OU", sync.Ad.GraveyardOu, SyncConfigSource),
                        CreateEntry("Leave OU", FormatOptional(sync.Ad.LeaveOu), SyncConfigSource)
                    ]),
                new AdminConfigurationGroupViewModel(
                    Title: "Identity",
                    Entries:
                    [
                        CreateEntry("Identity attribute", sync.Ad.IdentityAttribute, SyncConfigSource),
                        CreateEntry("UPN suffix", sync.Ad.UpnSuffix, SyncConfigSource),
                        CreateEntry("Licensing groups", FormatCollection(sync.Ad.LicensingGroups ?? []), SyncConfigSource),
                        CreateEntry(
                            "Resolve conflicting UPN/mail",
                            FormatEnabledDisabled(sync.Ad.IdentityPolicy.ResolveCreateConflictingUpnAndMail),
                            SyncConfigSource),
                        CreateEntry(
                            "Identity correlation",
                            FormatEnabledDisabled(sync.Ad.IdentityCorrelation?.Enabled ?? false),
                            SyncConfigSource),
                        CreateEntry(
                            "Successor personIdExternal attribute",
                            FormatOptional(sync.Ad.IdentityCorrelation?.SuccessorPersonIdExternalAttribute),
                            SyncConfigSource),
                        CreateEntry(
                            "Previous personIdExternal attribute",
                            FormatOptional(sync.Ad.IdentityCorrelation?.PreviousPersonIdExternalAttribute),
                            SyncConfigSource)
                    ]),
                new AdminConfigurationGroupViewModel(
                    Title: "Transport",
                    Entries:
                    [
                        CreateEntry("Mode", sync.Ad.Transport.Mode, SyncConfigSource),
                        CreateEntry(
                            "Allow LDAP fallback",
                            FormatEnabledDisabled(sync.Ad.Transport.AllowLdapFallback),
                            SyncConfigSource),
                        CreateEntry(
                            "Allow create-time enable without password",
                            FormatEnabledDisabled(sync.Ad.Transport.AllowCreateEnableWithoutPasswordProvisioning),
                            SyncConfigSource),
                        CreateEntry(
                            "Require certificate validation",
                            FormatEnabledDisabled(sync.Ad.Transport.RequireCertificateValidation),
                            SyncConfigSource),
                        CreateEntry(
                            "Require signing",
                            FormatEnabledDisabled(sync.Ad.Transport.RequireSigning),
                            SyncConfigSource),
                        CreateEntry(
                            "Trusted certificate thumbprints",
                            FormatCollection(sync.Ad.Transport.TrustedCertificateThumbprints),
                            SyncConfigSource)
                    ])
            ]);
    }

    private AdminConfigurationSectionViewModel BuildOperationsSection(SyncFactorsConfigDocument sync)
    {
        var smtpEntries = sync.Alerts.Smtp is null
            ? new[]
            {
                CreateEntry("SMTP", NotConfiguredValue, SyncConfigSource)
            }
            : new[]
            {
                CreateEntry("Host", sync.Alerts.Smtp.Host, SyncConfigSource),
                CreateEntry("Port", sync.Alerts.Smtp.Port.ToString(), SyncConfigSource),
                CreateEntry("SSL", FormatEnabledDisabled(sync.Alerts.Smtp.UseSsl), SyncConfigSource),
                CreateEntry("From", sync.Alerts.Smtp.From, SyncConfigSource),
                CreateEntry("Recipients", FormatCollection(sync.Alerts.Smtp.To), SyncConfigSource)
            };

        return new AdminConfigurationSectionViewModel(
            Id: "operations",
            Eyebrow: "Policies",
            Title: "Sync, Safety, Alerts, and Reporting",
            Description: "Operational policies, guardrails, alert delivery settings, and reporting output paths.",
            Groups:
            [
                new AdminConfigurationGroupViewModel(
                    Title: "Sync Policy",
                    Entries:
                    [
                        CreateEntry("Enable before start days", sync.Sync.EnableBeforeStartDays.ToString(), SyncConfigSource),
                        CreateEntry("Deletion retention days", sync.Sync.DeletionRetentionDays.ToString(), SyncConfigSource),
                        CreateEntry("Max degree of parallelism", sync.Sync.MaxDegreeOfParallelism.ToString(), SyncConfigSource),
                        CreateEntry("Real sync enabled", FormatEnabledDisabled(sync.Sync.RealSyncEnabled), SyncConfigSource),
                        CreateEntry("Auto-delete from graveyard", FormatEnabledDisabled(sync.Sync.AutoDeleteFromGraveyard), SyncConfigSource),
                        CreateEntry("Leave status values", FormatCollection(sync.Sync.LeaveStatusValues), SyncConfigSource)
                    ]),
                new AdminConfigurationGroupViewModel(
                    Title: "Safety Guardrails",
                    Entries:
                    [
                        CreateEntry("Max creates per run", sync.Safety.MaxCreatesPerRun.ToString(), SyncConfigSource),
                        CreateEntry("Max disables per run", sync.Safety.MaxDisablesPerRun.ToString(), SyncConfigSource),
                        CreateEntry("Max deletions per run", sync.Safety.MaxDeletionsPerRun.ToString(), SyncConfigSource)
                    ]),
                new AdminConfigurationGroupViewModel(
                    Title: "Alerts",
                    Entries:
                    [
                        CreateEntry("Alerts enabled", FormatEnabledDisabled(sync.Alerts.Enabled), SyncConfigSource),
                        CreateEntry("Subject prefix", sync.Alerts.SubjectPrefix, SyncConfigSource),
                        CreateEntry("Graveyard retention report", FormatEnabledDisabled(sync.Alerts.GraveyardRetentionReport.Enabled), SyncConfigSource),
                        CreateEntry("Report interval", FormatDays(sync.Alerts.GraveyardRetentionReport.IntervalDays), SyncConfigSource),
                        .. smtpEntries
                    ]),
                new AdminConfigurationGroupViewModel(
                    Title: "Reporting",
                    Entries:
                    [
                        CreateEntry("Output directory", sync.Reporting.OutputDirectory, SyncConfigSource)
                    ])
            ]);
    }

    private AdminConfigurationSectionViewModel BuildMappingsSection(MappingConfigDocument mapping)
    {
        var rows = mapping.Mappings
            .Select(item => new AdminConfigurationMappingRowViewModel(
                Source: item.Source,
                Target: item.Target,
                Enabled: item.Enabled,
                Required: item.Required,
                Transform: item.Transform))
            .ToArray();

        return new AdminConfigurationSectionViewModel(
            Id: "attribute-mappings",
            Eyebrow: "Projection",
            Title: "Attribute Mappings",
            Description: "All configured source-to-target mappings, including disabled entries that remain in the mapping file.",
            Groups: [],
            MappingSummary: new AdminConfigurationMappingSummaryViewModel(
                TotalCount: rows.Length,
                EnabledCount: rows.Count(row => row.Enabled),
                DisabledCount: rows.Count(row => !row.Enabled),
                RequiredCount: rows.Count(row => row.Required)),
            MappingRows: rows);
    }

    private static AdminConfigurationGroupViewModel BuildQueryGroup(string title, SuccessFactorsQueryConfig query)
    {
        return new AdminConfigurationGroupViewModel(
            Title: title,
            Entries:
            [
                CreateEntry("Entity set", query.EntitySet, SyncConfigSource),
                CreateEntry("Identity field", query.IdentityField, SyncConfigSource),
                CreateEntry("Delta field", FormatOptional(query.DeltaField), SyncConfigSource),
                CreateEntry("Delta sync enabled", FormatEnabledDisabled(query.DeltaSyncEnabled), SyncConfigSource),
                CreateEntry("Delta overlap", FormatMinutes(query.DeltaOverlapMinutes), SyncConfigSource),
                CreateEntry("Base filter", FormatOptional(query.BaseFilter), SyncConfigSource),
                CreateEntry("Order by", FormatOptional(query.OrderBy), SyncConfigSource),
                CreateEntry("Inactive retention", query.InactiveRetentionDays is null ? NotConfiguredValue : FormatDays(query.InactiveRetentionDays.Value), SyncConfigSource),
                CreateEntry("Inactive status field", query.InactiveStatusField, SyncConfigSource),
                CreateEntry("Inactive status values", FormatCollection(query.InactiveStatusValues), SyncConfigSource),
                CreateEntry("Inactive date field", query.InactiveDateField, SyncConfigSource),
                CreateEntry("As-of date", FormatOptional(query.AsOfDate), SyncConfigSource),
                CreateEntry("Page size", query.PageSize.ToString(), SyncConfigSource),
                CreateEntry("Select fields", FormatCollection(query.Select), SyncConfigSource),
                CreateEntry("Expand paths", FormatCollection(query.Expand), SyncConfigSource)
            ]);
    }

    private static AdminConfigurationEntryViewModel CreateEntry(
        string label,
        string displayValue,
        string sourceLabel,
        bool isSecretOmitted = false) =>
        new(label, displayValue, sourceLabel, isSecretOmitted);

    private static string FormatResolvedPathValue(string? configuredPath, string defaultDescription, string? resolvedPath)
    {
        var configuredLine = string.IsNullOrWhiteSpace(configuredPath)
            ? $"Configured: Default ({defaultDescription})"
            : $"Configured: {configuredPath}";
        var resolvedLine = $"Resolved: {resolvedPath ?? "Unavailable"}";
        return string.Join(Environment.NewLine, configuredLine, resolvedLine);
    }

    private static string FormatOptional(string? value) =>
        string.IsNullOrWhiteSpace(value) ? NotConfiguredValue : value.Trim();

    private static string FormatCollection(IEnumerable<string>? values)
    {
        if (values is null)
        {
            return NotConfiguredValue;
        }

        var entries = values
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value.Trim())
            .ToArray();
        return entries.Length == 0 ? NotConfiguredValue : string.Join(", ", entries);
    }

    private static string FormatEnabledDisabled(bool value) => value ? "Enabled" : "Disabled";

    private static string FormatHours(int value) => value == 1 ? "1 hour" : $"{value} hours";

    private static string FormatMinutes(int value) => value == 1 ? "1 minute" : $"{value} minutes";

    private static string FormatDays(int value) => value == 1 ? "1 day" : $"{value} days";

    private static string FormatHiddenOrMissing(string? value) =>
        string.IsNullOrWhiteSpace(value) ? NotConfiguredValue : HiddenValue;

    private static string FormatAuthenticationMode(string? mode)
    {
        return mode?.Trim().ToLowerInvariant() switch
        {
            "oidc" => "SSO only",
            "hybrid" => "SSO + break-glass",
            "local-break-glass" => "Local break-glass only",
            _ => NotConfiguredValue
        };
    }

    private string ResolveRunProfile()
    {
        var configuredProfile = configuration["SYNCFACTORS_RUN_PROFILE"];
        return string.Equals(configuredProfile, "real", StringComparison.OrdinalIgnoreCase)
            ? "real"
            : "mock";
    }

    private bool IsApplicationInsightsConfigured()
    {
        return HasConfiguredValue("APPLICATIONINSIGHTS_CONNECTION_STRING")
            || HasConfiguredValue("ApplicationInsights:ConnectionString")
            || HasConfiguredValue("APPINSIGHTS_INSTRUMENTATIONKEY")
            || HasConfiguredValue("ApplicationInsights:InstrumentationKey");
    }

    private static bool IsLocalAuthenticationEnabled(LocalAuthOptions options)
    {
        return string.Equals(options.Mode, "local-break-glass", StringComparison.OrdinalIgnoreCase)
            || string.Equals(options.Mode, "hybrid", StringComparison.OrdinalIgnoreCase)
            || options.LocalBreakGlass.Enabled;
    }

    private string ResolveSecretSource(string? environmentVariableName, string fallbackSource) =>
        HasEnvironmentValue(environmentVariableName)
            ? environmentVariableName!
            : fallbackSource;

    private static bool HasEnvironmentValue(string? environmentVariableName)
    {
        if (string.IsNullOrWhiteSpace(environmentVariableName))
        {
            return false;
        }

        return !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(environmentVariableName));
    }

    private string GetHostSource(params string[] keys) =>
        keys.Any(HasConfiguredValue) ? HostConfigSource : DefaultSource;

    private bool HasConfiguredValue(string key) => configuration.GetSection(key).Exists();
}

internal sealed record AdminConfigurationPageSnapshot(
    IReadOnlyList<AdminConfigurationSectionViewModel> Sections);

internal sealed record AdminConfigurationSectionViewModel(
    string Id,
    string Eyebrow,
    string Title,
    string Description,
    IReadOnlyList<AdminConfigurationGroupViewModel> Groups,
    AdminConfigurationMappingSummaryViewModel? MappingSummary = null,
    IReadOnlyList<AdminConfigurationMappingRowViewModel>? MappingRows = null);

internal sealed record AdminConfigurationGroupViewModel(
    string Title,
    IReadOnlyList<AdminConfigurationEntryViewModel> Entries);

internal sealed record AdminConfigurationEntryViewModel(
    string Label,
    string DisplayValue,
    string SourceLabel,
    bool IsSecretOmitted);

internal sealed record AdminConfigurationMappingSummaryViewModel(
    int TotalCount,
    int EnabledCount,
    int DisabledCount,
    int RequiredCount);

internal sealed record AdminConfigurationMappingRowViewModel(
    string Source,
    string Target,
    bool Enabled,
    bool Required,
    string Transform);

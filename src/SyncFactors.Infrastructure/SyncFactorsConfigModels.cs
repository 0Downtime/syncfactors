namespace SyncFactors.Infrastructure;

public sealed record SyncFactorsConfigDocument(
    SecretsConfig Secrets,
    SuccessFactorsConfig SuccessFactors,
    ActiveDirectoryConfig Ad,
    SyncPolicyConfig Sync,
    SafetyConfig Safety,
    AlertingConfig Alerts,
    ReportingConfig Reporting);

public sealed record SecretsConfig(
    string? SuccessFactorsUsernameEnv,
    string? SuccessFactorsPasswordEnv,
    string? SuccessFactorsClientIdEnv,
    string? SuccessFactorsClientSecretEnv,
    string? AdServerEnv,
    string? AdUsernameEnv,
    string? AdBindPasswordEnv);

public sealed record SuccessFactorsConfig(
    string BaseUrl,
    SuccessFactorsAuthConfig Auth,
    SuccessFactorsQueryConfig Query,
    SuccessFactorsQueryConfig? PreviewQuery);

public sealed record SuccessFactorsAuthConfig(
    string Mode,
    SuccessFactorsBasicAuthConfig? Basic,
    SuccessFactorsOAuthConfig? OAuth);

public sealed record SuccessFactorsBasicAuthConfig(
    string Username,
    string Password);

public sealed record SuccessFactorsOAuthConfig(
    string TokenUrl,
    string ClientId,
    string ClientSecret,
    string? CompanyId);

public sealed record SuccessFactorsQueryConfig(
    string EntitySet,
    string IdentityField,
    string DeltaField,
    bool DeltaSyncEnabled,
    int DeltaOverlapMinutes,
    string? BaseFilter,
    string? OrderBy,
    int? InactiveRetentionDays,
    string InactiveStatusField,
    IReadOnlyList<string> InactiveStatusValues,
    string InactiveDateField,
    string? AsOfDate,
    int PageSize,
    IReadOnlyList<string> Select,
    IReadOnlyList<string> Expand);

public sealed record ActiveDirectoryConfig(
    string Server,
    int? Port,
    string? Username,
    string? BindPassword,
    string IdentityAttribute,
    string DefaultActiveOu,
    string PrehireOu,
    string GraveyardOu,
    ActiveDirectoryTransportConfig Transport,
    ActiveDirectoryIdentityPolicyConfig IdentityPolicy,
    string? LeaveOu = null);

public sealed record ActiveDirectoryTransportConfig(
    string Mode,
    bool AllowLdapFallback,
    bool RequireCertificateValidation,
    bool RequireSigning,
    IReadOnlyList<string> TrustedCertificateThumbprints);

public sealed record ActiveDirectoryIdentityPolicyConfig(
    bool ResolveCreateConflictingUpnAndMail);

public sealed record SyncPolicyConfig(
    int EnableBeforeStartDays,
    int DeletionRetentionDays,
    IReadOnlyList<string>? LeaveStatusValues = null,
    bool SkipCreateIfPastDeletionRetention = false);

public sealed record SafetyConfig(
    int MaxCreatesPerRun,
    int MaxDisablesPerRun,
    int MaxDeletionsPerRun);

public sealed record AlertingConfig(
    bool Enabled,
    string SubjectPrefix,
    GraveyardRetentionReportConfig GraveyardRetentionReport,
    SmtpConfig? Smtp);

public sealed record GraveyardRetentionReportConfig(
    bool Enabled,
    int IntervalDays);

public sealed record SmtpConfig(
    string Host,
    int Port,
    bool UseSsl,
    string From,
    IReadOnlyList<string> To);

public sealed record ReportingConfig(
    string OutputDirectory);

public sealed record MappingConfigDocument(
    IReadOnlyList<AttributeMappingConfig> Mappings);

public sealed record AttributeMappingConfig(
    string Source,
    string Target,
    bool Enabled,
    bool Required,
    string Transform);

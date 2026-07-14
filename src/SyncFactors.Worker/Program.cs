using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using SyncFactors.Domain;
using SyncFactors.Infrastructure;
using SyncFactors.Worker;
using System.Net;

const string WindowsServiceName = "SyncFactors.Worker";

var builder = Host.CreateApplicationBuilder(args);
ConfigureWindowsService(builder.Services, WindowsServiceName);
LocalFileLogging.Configure(
    builder.Logging,
    processName: "worker",
    enabledValue: builder.Configuration[LocalFileLogging.EnabledEnvironmentVariable],
    directoryValue: builder.Configuration[LocalFileLogging.DirectoryEnvironmentVariable],
    retainedFileCountLimitValue: builder.Configuration[LocalFileLogging.RetainedFileCountLimitEnvironmentVariable],
    runLoggingEnabledValue: builder.Configuration[LocalFileLogging.RunFileLoggingEnabledEnvironmentVariable],
    runRetainedFileCountLimitValue: builder.Configuration[LocalFileLogging.RunRetainedFileCountLimitEnvironmentVariable],
    retentionDaysValue: builder.Configuration[LocalFileLogging.RetentionDaysEnvironmentVariable]);
ConfigureApplicationInsights(builder);
builder.Services.AddSingleton(new ScaffoldDataPathResolver(builder.Configuration["SyncFactors:ScaffoldDataPath"]));
builder.Services.AddSingleton(new SqlitePathResolver(builder.Configuration["SyncFactors:SqlitePath"]));
builder.Services.AddSingleton(new SyncFactorsConfigPathResolver(
    builder.Configuration["SyncFactors:ConfigPath"],
    builder.Configuration["SyncFactors:MappingConfigPath"]));
builder.Services.AddSingleton<SqliteDatabaseInitializer>();
builder.Services.AddSingleton<SyncFactorsConfigurationLoader>();
builder.Services.AddSingleton<SyncFactorsConfigurationValidator>();
builder.Services.AddSingleton<IEmailAddressPolicy, ConfiguredEmailAddressPolicy>();
builder.Services.AddSingleton<ScaffoldDataStore>();
builder.Services.AddSingleton<ScaffoldWorkerSource>();
builder.Services.AddSingleton(serviceProvider =>
{
    var config = serviceProvider.GetRequiredService<SyncFactorsConfigurationLoader>().GetSyncConfig();
    return new SyncFactors.Contracts.WorkerRunSettings(
        config.Safety.MaxCreatesPerRun,
        config.Safety.MaxDisablesPerRun,
        config.Safety.MaxDeletionsPerRun,
        ManualReviewRequired(config, "DisableUser", "MoveToGraveyardOu"),
        ManualReviewRequired(config, "DeleteUser"));
});
builder.Services.AddSingleton(serviceProvider =>
{
    var config = serviceProvider.GetRequiredService<SyncFactorsConfigurationLoader>().GetSyncConfig();
    return new SyncFactors.Contracts.RunHistoryRetentionSettings(
        config.Sync.RunHistoryRetentionDays,
        config.Sync.RunHistoryVacuumEnabled,
        config.Sync.RunHistoryVacuumMinimumFreedMegabytes,
        config.Sync.RunHistoryVacuumMinimumIntervalHours);
});
builder.Services.AddSingleton(serviceProvider =>
{
    var config = serviceProvider.GetRequiredService<SyncFactorsConfigurationLoader>().GetSyncConfig();
    var dryRunOnly = serviceProvider.GetRequiredService<IConfiguration>()
        .GetValue<bool?>("SyncFactors:Runtime:DryRunOnly") ?? false;
    return new SyncFactors.Contracts.RealSyncSettings(config.Sync.RealSyncEnabled, dryRunOnly);
});
builder.Services.AddSingleton(serviceProvider =>
{
    var config = serviceProvider.GetRequiredService<SyncFactorsConfigurationLoader>().GetSyncConfig();
    return new SyncFactors.Contracts.GraveyardDeletionQueueSettings(
        RetentionDays: config.Sync.DeletionRetentionDays,
        AutoDeleteEnabled: config.Sync.AutoDeleteFromGraveyard);
});
builder.Services.AddSingleton(serviceProvider =>
{
    var config = serviceProvider.GetRequiredService<SyncFactorsConfigurationLoader>().GetSyncConfig();
    return new SyncFactors.Contracts.LifecyclePolicySettings(
        config.Ad.DefaultActiveOu,
        config.Ad.PrehireOu,
        config.Ad.GraveyardOu,
        config.SuccessFactors.Query.InactiveStatusField,
        config.SuccessFactors.Query.InactiveStatusValues,
        config.Ad.LeaveOu,
        config.Sync.LeaveStatusValues,
        config.Ad.IdentityAttribute);
});
builder.Services.AddSingleton(serviceProvider =>
{
    var config = serviceProvider.GetRequiredService<SyncFactorsConfigurationLoader>().GetSyncConfig();
    var identityCorrelation = config.Ad.IdentityCorrelation;
    return new SyncFactors.Contracts.IdentityCorrelationSettings(
        identityCorrelation?.Enabled ?? false,
        config.Ad.IdentityAttribute,
        identityCorrelation?.SuccessorPersonIdExternalAttribute,
        identityCorrelation?.PreviousPersonIdExternalAttribute);
});
builder.Services.AddSingleton(serviceProvider =>
{
    var config = serviceProvider.GetRequiredService<SyncFactorsConfigurationLoader>().GetSyncConfig();
    return new SyncFactors.Contracts.GraveyardRetentionNotificationSettings(
        Enabled: config.Alerts.Enabled && config.Alerts.GraveyardRetentionReport.Enabled,
        IntervalDays: config.Alerts.GraveyardRetentionReport.IntervalDays,
        RetentionDays: config.Sync.DeletionRetentionDays,
        SubjectPrefix: config.Alerts.SubjectPrefix,
        Recipients: config.Alerts.Smtp?.To ?? []);
});
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<IRuntimeStatusStore, SqliteRuntimeStatusStore>();
builder.Services.AddSingleton<IDeltaSyncStateStore, SqliteDeltaSyncStateStore>();
builder.Services.AddSingleton<IDeltaSyncService, SuccessFactorsDeltaSyncService>();
builder.Services.AddSingleton<IWorkerHeartbeatStore, SqliteWorkerHeartbeatStore>();
builder.Services.AddSingleton<IRunRepository, SqliteRunRepository>();
builder.Services.AddSingleton<IRunQueueStore, SqliteRunQueueStore>();
builder.Services.AddSingleton<RunQueueRecoveryService>();
builder.Services.AddSingleton<ISyncScheduleStore, SqliteSyncScheduleStore>();
builder.Services.AddSingleton<SqliteGraveyardRetentionStore>();
builder.Services.AddSingleton<IGraveyardRetentionStore>(serviceProvider => serviceProvider.GetRequiredService<SqliteGraveyardRetentionStore>());
builder.Services.AddHttpClient<SuccessFactorsWorkerSource>()
    .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler
    {
        AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate
    });
builder.Services.AddTransient<IWorkerSource>(serviceProvider => serviceProvider.GetRequiredService<SuccessFactorsWorkerSource>());
builder.Services.AddDirectoryServiceRuntimeGateways(builder.Configuration["SYNCFACTORS_RUN_PROFILE"]);
builder.Services.AddTransient<IEmailSender, SmtpEmailSender>();
builder.Services.AddSingleton<IAttributeMappingProvider, AttributeMappingProvider>();
builder.Services.AddSingleton<IIdentityMatcher, IdentityMatcher>();
builder.Services.AddSingleton<ILifecyclePolicy, LifecyclePolicy>();
builder.Services.AddSingleton<IActiveDirectoryConnectionPool, ActiveDirectoryConnectionPool>();
builder.Services.AddSingleton<IWorkerPreviewLogWriter, FileWorkerPreviewLogWriter>();
builder.Services.AddTransient<IAttributeDiffService, AttributeDiffService>();
builder.Services.AddSingleton<IRunCaptureMetadataProvider, RunCaptureMetadataProvider>();
builder.Services.AddTransient<IWorkerPlanningService, WorkerPlanningService>();
builder.Services.AddSingleton<IDirectoryMutationCommandBuilder, DirectoryMutationCommandBuilder>();
builder.Services.AddTransient<BulkRunCoordinator>();
builder.Services.AddTransient<DeleteAllUsersCoordinator>();
builder.Services.AddTransient<GraveyardDeletionQueueService>();
builder.Services.AddTransient<GraveyardAutoDeleteCoordinator>();
builder.Services.AddTransient<SyncScheduleCoordinator>();
builder.Services.AddTransient<GraveyardRetentionReportCoordinator>();
builder.Services.AddSingleton<IRunLifecycleService, RunLifecycleService>();
builder.Services.AddHostedService<Worker>();

var host = builder.Build();
await host.Services.GetRequiredService<SqliteDatabaseInitializer>().InitializeAsync(CancellationToken.None);
host.Services.GetRequiredService<SyncFactorsConfigurationValidator>().Validate();
await host.Services.GetRequiredService<RunQueueRecoveryService>().RecoverIfNeededAsync("worker startup", CancellationToken.None);
LogRuntimeVersion(host);
LogConfiguredEndpoints(host);
await host.RunAsync();

static void LogRuntimeVersion(IHost host)
{
    var buildInfo = RuntimeBuildInfo.FromAssembly(typeof(Program).Assembly);
    var logger = host.Services.GetRequiredService<ILoggerFactory>().CreateLogger("SyncFactors.Worker.Startup");
    logger.LogInformation(
        "SyncFactors worker starting. Version={Version} CommitSha={CommitSha} Dirty={Dirty}",
        buildInfo.Version,
        buildInfo.CommitSha ?? "unknown",
        buildInfo.Dirty);
}

static void LogConfiguredEndpoints(IHost host)
{
    var config = host.Services.GetRequiredService<SyncFactorsConfigurationLoader>().GetSyncConfig();
    var logger = host.Services.GetRequiredService<ILoggerFactory>().CreateLogger("SyncFactors.Worker.Startup");
    var activeDirectoryPort = ResolveActiveDirectoryPort(config.Ad);
    var usesGlobalCatalog = activeDirectoryPort is 3268 or 3269;

    logger.LogInformation(
        "[AD-TRANSPORT] Active Directory startup transport: {ActiveDirectoryStartupTransport}.",
        ActiveDirectoryTransportModeFormatter.DescribeStartupTransport(config.Ad.Transport.Mode));
    logger.LogWarning("========== AD ENDPOINT DIAGNOSTIC ==========");
    logger.LogWarning(
        "[AD-ENDPOINT] ActiveDirectoryServerConfigured={ActiveDirectoryServerConfigured} ActiveDirectoryPort={ActiveDirectoryPort} ActiveDirectoryAccountConfigured={ActiveDirectoryAccountConfigured} ActiveDirectorySimpleBindPrincipalFormat={ActiveDirectorySimpleBindPrincipalFormat} ActiveDirectoryTransport={ActiveDirectoryTransport} ActiveDirectoryUsesGlobalCatalog={ActiveDirectoryUsesGlobalCatalog} SuccessFactorsBaseUrlConfigured={SuccessFactorsBaseUrlConfigured} SuccessFactorsAccountConfigured={SuccessFactorsAccountConfigured} SuccessFactorsAuthMode={SuccessFactorsAuthMode}",
        DescribeConfiguredValue(config.Ad.Server),
        activeDirectoryPort,
        DescribeConfiguredValue(config.Ad.Username),
        DescribeSimpleBindPrincipalFormat(config.Ad.Username),
        config.Ad.Transport.Mode,
        usesGlobalCatalog,
        DescribeConfiguredValue(config.SuccessFactors.BaseUrl),
        DescribeSuccessFactorsAccountConfiguration(config.SuccessFactors.Auth),
        config.SuccessFactors.Auth.Mode);
    logger.LogWarning("============================================");

    if (usesGlobalCatalog)
    {
        logger.LogCritical(
            "[AD-ENDPOINT] Active Directory is configured to use Global Catalog port {ActiveDirectoryPort}. Attributes outside the partial attribute set, especially custom identity attributes such as employeeID, may read back as empty.",
            activeDirectoryPort);
    }
}

static int ResolveActiveDirectoryPort(ActiveDirectoryConfig config)
{
    if (config.Port is not null)
    {
        return config.Port.Value;
    }

    return string.Equals(config.Transport.Mode, "ldaps", StringComparison.OrdinalIgnoreCase) ? 636 : 389;
}

static string DescribeSimpleBindPrincipalFormat(string? username)
{
    if (string.IsNullOrWhiteSpace(username))
    {
        return "Anonymous";
    }

    var trimmed = username.Trim();
    if (trimmed.Contains('@', StringComparison.Ordinal))
    {
        return "UPN";
    }

    if (trimmed.Contains('=', StringComparison.Ordinal) && trimmed.Contains(',', StringComparison.Ordinal))
    {
        return "DN";
    }

    if (trimmed.Contains('\\', StringComparison.Ordinal))
    {
        return "DownLevel";
    }

    return "BareUsername";
}

static string DescribeConfiguredValue(string? value) =>
    string.IsNullOrWhiteSpace(value) ? "Missing" : "Configured";

static string DescribeSuccessFactorsAccountConfiguration(SuccessFactorsAuthConfig auth)
{
    if (string.Equals(auth.Mode, "basic", StringComparison.OrdinalIgnoreCase) && auth.Basic is not null)
    {
        return DescribeConfiguredValue(auth.Basic.Username);
    }

    if (string.Equals(auth.Mode, "oauth", StringComparison.OrdinalIgnoreCase) && auth.OAuth is not null)
    {
        return DescribeConfiguredValue(auth.OAuth.ClientId);
    }

    return "Missing";
}

static void ConfigureWindowsService(IServiceCollection services, string serviceName)
{
    services.AddWindowsService(options =>
    {
        options.ServiceName = serviceName;
    });

    if (OperatingSystem.IsWindows())
    {
        ConfigureWindowsEventLog(services, serviceName);
    }
}

[System.Runtime.Versioning.SupportedOSPlatform("windows")]
static void ConfigureWindowsEventLog(IServiceCollection services, string serviceName)
{
    services.Configure<Microsoft.Extensions.Logging.EventLog.EventLogSettings>(options =>
    {
#pragma warning disable CA1416
        options.LogName = "Application";
        options.SourceName = serviceName;
#pragma warning restore CA1416
    });
}

static bool ManualReviewRequired(SyncFactorsConfigDocument config, params string[] operationKinds)
{
    return config.Approval.Enabled &&
           operationKinds.Any(operationKind =>
               config.Approval.RequireFor.Any(required =>
                   string.Equals(required, operationKind, StringComparison.OrdinalIgnoreCase)));
}

static void ConfigureApplicationInsights(HostApplicationBuilder builder)
{
    if (!IsApplicationInsightsConfigured(builder.Configuration))
    {
        return;
    }

    builder.Services.AddApplicationInsightsTelemetryWorkerService();
    RemoveApplicationInsightsDefaultWarningFilter(builder.Logging);
}

static bool IsApplicationInsightsConfigured(ConfigurationManager configuration)
{
    return !string.IsNullOrWhiteSpace(configuration["APPLICATIONINSIGHTS_CONNECTION_STRING"])
        || !string.IsNullOrWhiteSpace(configuration["ApplicationInsights:ConnectionString"])
        || !string.IsNullOrWhiteSpace(configuration["APPINSIGHTS_INSTRUMENTATIONKEY"])
        || !string.IsNullOrWhiteSpace(configuration["ApplicationInsights:InstrumentationKey"]);
}

static void RemoveApplicationInsightsDefaultWarningFilter(ILoggingBuilder logging)
{
    logging.Services.Configure<LoggerFilterOptions>(options =>
    {
        const string ProviderName = "Microsoft.Extensions.Logging.ApplicationInsights.ApplicationInsightsLoggerProvider";
        var defaultRule = options.Rules.FirstOrDefault(rule => string.Equals(rule.ProviderName, ProviderName, StringComparison.Ordinal));
        if (defaultRule is not null)
        {
            options.Rules.Remove(defaultRule);
        }
    });
}

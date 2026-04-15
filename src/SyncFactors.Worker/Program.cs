using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Serilog;
using SyncFactors.Domain;
using SyncFactors.Infrastructure;
using System.Net;

var builder = Host.CreateApplicationBuilder(args);
ConfigureLocalFileLogging(
    builder.Logging,
    processName: "worker",
    enabledValue: builder.Configuration[LocalFileLogging.EnabledEnvironmentVariable],
    directoryValue: builder.Configuration[LocalFileLogging.DirectoryEnvironmentVariable]);
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
    return new SyncFactors.Contracts.WorkerRunSettings(config.Safety.MaxCreatesPerRun, config.Safety.MaxDisablesPerRun, config.Safety.MaxDeletionsPerRun);
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
builder.Services.AddSingleton<IGraveyardRetentionStore, SqliteGraveyardRetentionStore>();
builder.Services.AddHttpClient<SuccessFactorsWorkerSource>()
    .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler
    {
        AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate
    });
builder.Services.AddTransient<IWorkerSource>(serviceProvider => serviceProvider.GetRequiredService<SuccessFactorsWorkerSource>());
builder.Services.AddTransient<IDirectoryGateway, ActiveDirectoryGateway>();
builder.Services.AddTransient<IDirectoryCommandGateway, ActiveDirectoryCommandGateway>();
builder.Services.AddTransient<IEmailSender, SmtpEmailSender>();
builder.Services.AddSingleton<IAttributeMappingProvider, AttributeMappingProvider>();
builder.Services.AddSingleton<IIdentityMatcher, IdentityMatcher>();
builder.Services.AddSingleton<ILifecyclePolicy, LifecyclePolicy>();
builder.Services.AddSingleton<IWorkerPreviewLogWriter, FileWorkerPreviewLogWriter>();
builder.Services.AddTransient<IAttributeDiffService, AttributeDiffService>();
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
LogConfiguredEndpoints(host);
host.Run();

static void LogConfiguredEndpoints(IHost host)
{
    var config = host.Services.GetRequiredService<SyncFactorsConfigurationLoader>().GetSyncConfig();
    var logger = host.Services.GetRequiredService<ILoggerFactory>().CreateLogger("SyncFactors.Worker.Startup");
    var activeDirectoryPort = ResolveActiveDirectoryPort(config.Ad);
    var usesGlobalCatalog = activeDirectoryPort is 3268 or 3269;

    logger.LogWarning("========== AD ENDPOINT DIAGNOSTIC ==========");
    logger.LogWarning(
        "[AD-ENDPOINT] ActiveDirectoryServer={ActiveDirectoryServer} ActiveDirectoryPort={ActiveDirectoryPort} ActiveDirectoryAccount={ActiveDirectoryAccount} ActiveDirectoryTransport={ActiveDirectoryTransport} ActiveDirectoryUsesGlobalCatalog={ActiveDirectoryUsesGlobalCatalog} SuccessFactorsBaseUrl={SuccessFactorsBaseUrl} SuccessFactorsAccount={SuccessFactorsAccount}",
        config.Ad.Server,
        activeDirectoryPort,
        string.IsNullOrWhiteSpace(config.Ad.Username) ? "anonymous" : config.Ad.Username,
        config.Ad.Transport.Mode,
        usesGlobalCatalog,
        config.SuccessFactors.BaseUrl,
        DescribeSuccessFactorsAccount(config.SuccessFactors.Auth));
    logger.LogWarning("============================================");

    if (usesGlobalCatalog)
    {
        logger.LogCritical(
            "[AD-ENDPOINT] Active Directory is configured to use Global Catalog port {ActiveDirectoryPort}. Attributes outside the partial attribute set, including employeeID by default, may read back as empty.",
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

static string DescribeSuccessFactorsAccount(SuccessFactorsAuthConfig auth)
{
    if (string.Equals(auth.Mode, "basic", StringComparison.OrdinalIgnoreCase) && auth.Basic is not null)
    {
        return auth.Basic.Username;
    }

    if (string.Equals(auth.Mode, "oauth", StringComparison.OrdinalIgnoreCase) && auth.OAuth is not null)
    {
        return string.IsNullOrWhiteSpace(auth.OAuth.CompanyId)
            ? $"oauth-client:{auth.OAuth.ClientId}"
            : $"oauth-client:{auth.OAuth.ClientId} company:{auth.OAuth.CompanyId}";
    }

    return $"mode:{auth.Mode}";
}

static void ConfigureLocalFileLogging(
    ILoggingBuilder logging,
    string processName,
    string? enabledValue,
    string? directoryValue)
{
    if (!LocalFileLogging.IsEnabled(enabledValue))
    {
        return;
    }

    var logPath = LocalFileLogging.ResolveRollingFilePath(processName, directoryValue);
    var logDirectory = Path.GetDirectoryName(logPath);
    if (!string.IsNullOrWhiteSpace(logDirectory))
    {
        Directory.CreateDirectory(logDirectory);
    }

    var logger = new LoggerConfiguration()
        .MinimumLevel.Verbose()
        .Enrich.FromLogContext()
        .WriteTo.File(
            path: logPath,
            rollingInterval: RollingInterval.Day,
            retainedFileCountLimit: 7,
            outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} [{Level:u3}] {SourceContext}: {Message:lj}{NewLine}{Exception}")
        .CreateLogger();

    logging.AddSerilog(logger, dispose: true);
}

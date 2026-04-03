using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using SyncFactors.Domain;
using SyncFactors.Infrastructure;
using System.Net;

var builder = Host.CreateApplicationBuilder(args);
builder.Services.AddSingleton(new ScaffoldDataPathResolver(builder.Configuration["SyncFactors:ScaffoldDataPath"]));
builder.Services.AddSingleton(new SqlitePathResolver(builder.Configuration["SyncFactors:SqlitePath"]));
builder.Services.AddSingleton(new SyncFactorsConfigPathResolver(
    builder.Configuration["SyncFactors:ConfigPath"],
    builder.Configuration["SyncFactors:MappingConfigPath"]));
builder.Services.AddSingleton<SqliteDatabaseInitializer>();
builder.Services.AddSingleton<SyncFactorsConfigurationLoader>();
builder.Services.AddSingleton<SyncFactorsConfigurationValidator>();
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
    return new SyncFactors.Contracts.LifecyclePolicySettings(
        config.Ad.DefaultActiveOu,
        config.Ad.PrehireOu,
        config.Ad.GraveyardOu,
        config.SuccessFactors.Query.InactiveStatusField,
        config.SuccessFactors.Query.InactiveStatusValues);
});
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<IRuntimeStatusStore, SqliteRuntimeStatusStore>();
builder.Services.AddSingleton<IDeltaSyncStateStore, SqliteDeltaSyncStateStore>();
builder.Services.AddSingleton<IDeltaSyncService, SuccessFactorsDeltaSyncService>();
builder.Services.AddSingleton<IWorkerHeartbeatStore, SqliteWorkerHeartbeatStore>();
builder.Services.AddSingleton<IRunRepository, SqliteRunRepository>();
builder.Services.AddSingleton<IRunQueueStore, SqliteRunQueueStore>();
builder.Services.AddSingleton<ISyncScheduleStore, SqliteSyncScheduleStore>();
builder.Services.AddHttpClient<SuccessFactorsWorkerSource>()
    .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler
    {
        AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate
    });
builder.Services.AddTransient<IWorkerSource>(serviceProvider => serviceProvider.GetRequiredService<SuccessFactorsWorkerSource>());
builder.Services.AddTransient<IDirectoryGateway, ActiveDirectoryGateway>();
builder.Services.AddTransient<IDirectoryCommandGateway, ActiveDirectoryCommandGateway>();
builder.Services.AddSingleton<IAttributeMappingProvider, AttributeMappingProvider>();
builder.Services.AddSingleton<IIdentityMatcher, IdentityMatcher>();
builder.Services.AddSingleton<ILifecyclePolicy, LifecyclePolicy>();
builder.Services.AddSingleton<IWorkerPreviewLogWriter, FileWorkerPreviewLogWriter>();
builder.Services.AddTransient<IAttributeDiffService, AttributeDiffService>();
builder.Services.AddTransient<IWorkerPlanningService, WorkerPlanningService>();
builder.Services.AddSingleton<IDirectoryMutationCommandBuilder, DirectoryMutationCommandBuilder>();
builder.Services.AddTransient<BulkRunCoordinator>();
builder.Services.AddTransient<DeleteAllUsersCoordinator>();
builder.Services.AddTransient<SyncScheduleCoordinator>();
builder.Services.AddSingleton<IRunLifecycleService, RunLifecycleService>();
builder.Services.AddHostedService<Worker>();

var host = builder.Build();
await host.Services.GetRequiredService<SqliteDatabaseInitializer>().InitializeAsync(CancellationToken.None);
host.Services.GetRequiredService<SyncFactorsConfigurationValidator>().Validate();
LogConfiguredWorkerStartup(host);
host.Run();

static void LogConfiguredWorkerStartup(IHost host)
{
    var configLoader = host.Services.GetRequiredService<SyncFactorsConfigurationLoader>();
    var config = configLoader.GetSyncConfig();
    var environment = host.Services.GetRequiredService<IHostEnvironment>();
    var configPathResolver = host.Services.GetRequiredService<SyncFactorsConfigPathResolver>();
    var logger = host.Services.GetRequiredService<Microsoft.Extensions.Logging.ILoggerFactory>()
        .CreateLogger("Startup");

    logger.LogInformation(
        "Worker startup configuration. Environment={Environment} SyncConfigPath={SyncConfigPath} MappingConfigPath={MappingConfigPath} ActiveDirectoryServer={ActiveDirectoryServer} ActiveDirectoryAccount={ActiveDirectoryAccount} ActiveDirectoryTransport={ActiveDirectoryTransport} SuccessFactorsBaseUrl={SuccessFactorsBaseUrl}",
        environment.EnvironmentName,
        configLoader.GetResolvedSyncConfigPath(),
        configPathResolver.ResolveMappingConfigPath(),
        config.Ad.Server,
        string.IsNullOrWhiteSpace(config.Ad.Username) ? "anonymous" : config.Ad.Username,
        config.Ad.Transport.Mode,
        config.SuccessFactors.BaseUrl);
}

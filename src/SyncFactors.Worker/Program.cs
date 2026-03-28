using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using SyncFactors.Domain;
using SyncFactors.Infrastructure;

var builder = Host.CreateApplicationBuilder(args);
builder.Services.AddSingleton(new SqlitePathResolver(builder.Configuration["SyncFactors:SqlitePath"]));
builder.Services.AddSingleton(new SyncFactorsConfigPathResolver(
    builder.Configuration["SyncFactors:ConfigPath"],
    builder.Configuration["SyncFactors:MappingConfigPath"]));
builder.Services.AddSingleton<SqliteDatabaseInitializer>();
builder.Services.AddSingleton<SyncFactorsConfigurationLoader>();
builder.Services.AddSingleton<SyncFactorsConfigurationValidator>();
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<IRuntimeStatusStore, SqliteRuntimeStatusStore>();
builder.Services.AddSingleton<IWorkerHeartbeatStore, SqliteWorkerHeartbeatStore>();
builder.Services.AddSingleton<IRunRepository, SqliteRunRepository>();
builder.Services.AddSingleton<IScaffoldRunPlanner, ScaffoldRunPlanner>();
builder.Services.AddSingleton<IRunLifecycleService, RunLifecycleService>();
builder.Services.AddHostedService<Worker>();

var host = builder.Build();
await host.Services.GetRequiredService<SqliteDatabaseInitializer>().InitializeAsync(CancellationToken.None);
host.Services.GetRequiredService<SyncFactorsConfigurationValidator>().Validate();
host.Run();

using SyncFactors.Contracts;
using SyncFactors.Domain;
using SyncFactors.Infrastructure;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddSingleton(new SqlitePathResolver(builder.Configuration["SyncFactors:SqlitePath"]));
builder.Services.AddSingleton(new SyncFactorsConfigPathResolver(
    builder.Configuration["SyncFactors:ConfigPath"],
    builder.Configuration["SyncFactors:MappingConfigPath"]));
builder.Services.AddSingleton(new ScaffoldDataPathResolver(builder.Configuration["SyncFactors:ScaffoldDataPath"]));
builder.Services.AddSingleton<SqliteDatabaseInitializer>();
builder.Services.AddSingleton<SyncFactorsConfigurationLoader>();
builder.Services.AddSingleton<SyncFactorsConfigurationValidator>();
builder.Services.AddSingleton<ScaffoldDataStore>();
builder.Services.AddSingleton<ScaffoldWorkerSource>();
builder.Services.AddSingleton<ScaffoldDirectoryGateway>();
builder.Services.AddSingleton<ScaffoldDirectoryCommandGateway>();
builder.Services.AddHttpClient<SuccessFactorsWorkerSource>();
builder.Services.AddTransient<IWorkerSource>(serviceProvider => serviceProvider.GetRequiredService<SuccessFactorsWorkerSource>());
builder.Services.AddTransient<IDirectoryGateway, ActiveDirectoryGateway>();
builder.Services.AddTransient<IDirectoryCommandGateway, ActiveDirectoryCommandGateway>();
builder.Services.AddSingleton<IIdentityMatcher, IdentityMatcher>();
builder.Services.AddSingleton<IAttributeDiffService, AttributeDiffService>();
builder.Services.AddTransient<IWorkerPreviewPlanner, WorkerPreviewPlanner>();
builder.Services.AddTransient<IApplyPreviewService, ApplyPreviewService>();
builder.Services.AddSingleton<IRuntimeStatusStore, SqliteRuntimeStatusStore>();
builder.Services.AddSingleton<IRunRepository, SqliteRunRepository>();
builder.Services.AddRazorPages();

var app = builder.Build();

await app.Services.GetRequiredService<SqliteDatabaseInitializer>().InitializeAsync(CancellationToken.None);
app.Services.GetRequiredService<SyncFactorsConfigurationValidator>().Validate();

app.UseStaticFiles();

app.MapGet("/api/status", async (IRuntimeStatusStore store, CancellationToken cancellationToken) =>
{
    var status = await store.GetCurrentAsync(cancellationToken)
        ?? new RuntimeStatus(
            Status: "Idle",
            Stage: "NotStarted",
            RunId: null,
            Mode: null,
            DryRun: true,
            ProcessedWorkers: 0,
            TotalWorkers: 0,
            CurrentWorkerId: null,
            LastAction: null,
            StartedAt: null,
            LastUpdatedAt: null,
            CompletedAt: null,
            ErrorMessage: null);

    return Results.Ok(new { status });
});

app.MapGet("/api/runs", async (IRunRepository repository, CancellationToken cancellationToken) =>
{
    var runs = await repository.ListRunsAsync(cancellationToken);
    return Results.Ok(new { runs });
});

app.MapGet("/api/runs/{runId}", async (string runId, IRunRepository repository, CancellationToken cancellationToken) =>
{
    var run = await repository.GetRunAsync(runId, cancellationToken);
    return run is null ? Results.NotFound() : Results.Ok(run);
});

app.MapGet("/api/runs/{runId}/entries", async (
    string runId,
    string? bucket,
    string? workerId,
    string? reason,
    string? filter,
    string? entryId,
    IRunRepository repository,
    CancellationToken cancellationToken) =>
{
    var run = await repository.GetRunAsync(runId, cancellationToken);
    if (run is null)
    {
        return Results.NotFound();
    }

    var entries = await repository.GetRunEntriesAsync(runId, bucket, workerId, reason, filter, entryId, cancellationToken);
    return Results.Ok(new { run = run.Run, entries, total = entries.Count });
});

app.MapPost("/api/preview/{workerId}/apply", async (
    string workerId,
    IApplyPreviewService applyPreviewService,
    CancellationToken cancellationToken) =>
{
    var result = await applyPreviewService.ApplyAsync(workerId, cancellationToken);
    return result.Succeeded
        ? Results.Ok(result)
        : Results.BadRequest(result);
});

app.MapRazorPages();

app.Run();

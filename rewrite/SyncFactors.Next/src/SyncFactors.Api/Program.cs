using SyncFactors.Contracts;
using SyncFactors.Domain;
using SyncFactors.Infrastructure;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddSingleton(new SqlitePathResolver(builder.Configuration["SyncFactors:SqlitePath"]));
builder.Services.AddSingleton<SqliteJsonShell>();
builder.Services.AddSingleton<IRuntimeStatusStore, SqliteRuntimeStatusStore>();
builder.Services.AddSingleton<IRunRepository, SqliteRunRepository>();
builder.Services.AddRazorPages();

var app = builder.Build();

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

app.MapRazorPages();

app.Run();

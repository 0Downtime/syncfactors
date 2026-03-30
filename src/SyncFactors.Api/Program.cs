using SyncFactors.Contracts;
using SyncFactors.Domain;
using SyncFactors.Infrastructure;
using System.Net;

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
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<IWorkerPreviewLogWriter, FileWorkerPreviewLogWriter>();
builder.Services.AddHttpClient<SuccessFactorsWorkerSource>()
    .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler
    {
        AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate
    });
builder.Services.AddHttpClient<IDependencyHealthService, DependencyHealthService>()
    .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler
    {
        AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate
    });
builder.Services.AddTransient<IWorkerSource>(serviceProvider => serviceProvider.GetRequiredService<SuccessFactorsWorkerSource>());
builder.Services.AddTransient<IDirectoryGateway, ActiveDirectoryGateway>();
builder.Services.AddTransient<IDirectoryCommandGateway, ActiveDirectoryCommandGateway>();
builder.Services.AddSingleton<IAttributeMappingProvider, AttributeMappingProvider>();
builder.Services.AddSingleton<IIdentityMatcher, IdentityMatcher>();
builder.Services.AddSingleton<IAttributeDiffService, AttributeDiffService>();
builder.Services.AddSingleton<IWorkerHeartbeatStore, SqliteWorkerHeartbeatStore>();
builder.Services.AddTransient<IWorkerPreviewPlanner, WorkerPreviewPlanner>();
builder.Services.AddTransient<IApplyPreviewService, ApplyPreviewService>();
builder.Services.AddSingleton<IDashboardSnapshotService, DashboardSnapshotService>();
builder.Services.AddSingleton<IRuntimeStatusStore, SqliteRuntimeStatusStore>();
builder.Services.AddSingleton<IRunRepository, SqliteRunRepository>();
builder.Services.AddRazorPages();

var app = builder.Build();

await app.Services.GetRequiredService<SqliteDatabaseInitializer>().InitializeAsync(CancellationToken.None);
app.Services.GetRequiredService<SyncFactorsConfigurationValidator>().Validate();
LogConfiguredEndpoints(app);

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

app.MapGet("/api/dashboard", async (IDashboardSnapshotService dashboardSnapshotService, CancellationToken cancellationToken) =>
{
    var snapshot = await dashboardSnapshotService.GetSnapshotAsync(cancellationToken);
    return Results.Ok(snapshot);
});

app.MapGet("/api/health", async (IDependencyHealthService healthService, CancellationToken cancellationToken) =>
{
    var snapshot = await healthService.GetSnapshotAsync(cancellationToken);
    return Results.Ok(snapshot);
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

app.MapGet("/api/previews/{runId}", async (string runId, IRunRepository repository, CancellationToken cancellationToken) =>
{
    var preview = await repository.GetWorkerPreviewAsync(runId, cancellationToken);
    return preview is null ? Results.NotFound() : Results.Ok(preview);
});

app.MapGet("/api/workers/{workerId}/previews", async (string workerId, int? take, IRunRepository repository, CancellationToken cancellationToken) =>
{
    var history = await repository.ListWorkerPreviewHistoryAsync(workerId, take ?? 6, cancellationToken);
    return Results.Ok(new { workerId, previews = history });
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
    ApplyPreviewRequest request,
    IApplyPreviewService applyPreviewService,
    CancellationToken cancellationToken) =>
{
    if (!string.Equals(workerId, request.WorkerId, StringComparison.Ordinal))
    {
        return Results.BadRequest(new { error = "Route worker id does not match the apply request." });
    }

    var result = await applyPreviewService.ApplyAsync(request, cancellationToken);
    return result.Succeeded
        ? Results.Ok(result)
        : Results.BadRequest(result);
});

app.MapRazorPages();

app.Run();

static void LogConfiguredEndpoints(WebApplication app)
{
    var config = app.Services.GetRequiredService<SyncFactorsConfigurationLoader>().GetSyncConfig();
    app.Logger.LogInformation(
        "Configured external endpoints. ActiveDirectoryServer={ActiveDirectoryServer} ActiveDirectoryAccount={ActiveDirectoryAccount} SuccessFactorsBaseUrl={SuccessFactorsBaseUrl} SuccessFactorsAccount={SuccessFactorsAccount}",
        config.Ad.Server,
        string.IsNullOrWhiteSpace(config.Ad.Username) ? "anonymous" : config.Ad.Username,
        config.SuccessFactors.BaseUrl,
        DescribeSuccessFactorsAccount(config.SuccessFactors.Auth));

    if (!string.IsNullOrWhiteSpace(config.Ad.Username) &&
        config.Ad.Username.Contains('\\', StringComparison.Ordinal))
    {
        app.Logger.LogWarning(
            "Active Directory is using LDAP simple bind with username '{ActiveDirectoryAccount}' in domain\\user format. If binds fail, prefer a UPN such as 'user@domain.example' for simple LDAP authentication.",
            config.Ad.Username);
    }

    if (!string.IsNullOrWhiteSpace(config.Ad.Username) &&
        config.Ad.Server.EndsWith(":389", StringComparison.Ordinal))
    {
        app.Logger.LogWarning(
            "Active Directory is configured for authenticated LDAP on port 389 at '{ActiveDirectoryServer}'. If the directory requires LDAPS or LDAP signing, binds may fail until the connection settings are updated.",
            config.Ad.Server);
    }
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

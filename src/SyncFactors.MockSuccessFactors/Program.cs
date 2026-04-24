using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.Extensions.Options;
using SyncFactors.Contracts;
using SyncFactors.Domain;
using SyncFactors.Infrastructure;
using SyncFactors.MockSuccessFactors;
using System.Net;

var command = FixtureGenerationCommand.TryParse(args);
if (command is not null)
{
    Environment.ExitCode = await FixtureGenerationCommand.RunAsync(command, Console.Out, CancellationToken.None);
    return;
}

var lifecycleSimulationCommand = LifecycleSimulationCommand.TryParse(args);
if (lifecycleSimulationCommand is not null)
{
    Environment.ExitCode = await LifecycleSimulationCommand.RunAsync(lifecycleSimulationCommand, Console.Out, CancellationToken.None);
    return;
}

var builder = WebApplication.CreateBuilder(args);
if (string.IsNullOrWhiteSpace(builder.Configuration["urls"]) &&
    string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("ASPNETCORE_URLS")))
{
    builder.WebHost.UseUrls("http://127.0.0.1:18080");
}

builder.Services.Configure<MockSuccessFactorsOptions>(builder.Configuration.GetSection("MockSuccessFactors"));
builder.Services.AddSingleton<IConfigureOptions<MockSuccessFactorsOptions>, MockSuccessFactorsOptionsSetup>();
builder.Services.AddSingleton<MockFixtureStore>();
builder.Services.AddSingleton<ODataResponseBuilder>();
builder.Services.AddSingleton<MockTokenService>();
builder.Services.AddSingleton(new SyncFactorsConfigPathResolver(
    builder.Configuration["SyncFactors:ConfigPath"],
    builder.Configuration["SyncFactors:MappingConfigPath"]));
builder.Services.AddSingleton(new MockRuntimeFixturePathResolver(builder.Configuration["MockSuccessFactors:Runtime:FixturePath"]));
builder.Services.AddSingleton<SyncFactorsConfigurationLoader>();
builder.Services.AddSingleton(new ScaffoldDataPathResolver(configuredPath: null));
builder.Services.AddSingleton<ScaffoldDataStore>();
builder.Services.AddSingleton<ScaffoldWorkerSource>();
builder.Services.AddSingleton<MockRuntimeFixtureReader>();
builder.Services.AddSingleton<IDeltaSyncService, MockDeltaSyncService>();
builder.Services.AddSingleton<IWorkerPreviewLogWriter, FileWorkerPreviewLogWriter>();
builder.Services.AddSingleton<IAttributeMappingProvider, AttributeMappingProvider>();
builder.Services.AddSingleton<IIdentityMatcher, IdentityMatcher>();
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
builder.Services.AddSingleton<ILifecyclePolicy, LifecyclePolicy>();
builder.Services.AddSingleton<IAttributeDiffService, AttributeDiffService>();
builder.Services.AddSingleton<IActiveDirectoryConnectionPool, ActiveDirectoryConnectionPool>();
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<RuntimeFixtureDirectoryGateway>();
builder.Services.AddTransient<IDirectoryGateway>(serviceProvider => serviceProvider.GetRequiredService<RuntimeFixtureDirectoryGateway>());
builder.Services.AddHttpClient<SuccessFactorsWorkerSource>()
    .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler
    {
        AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate
    });
builder.Services.AddTransient<IWorkerPlanningService, WorkerPlanningService>();
builder.Services.AddTransient<MockPlannerComparisonService>();
builder.Services.AddRazorPages();

var app = builder.Build();
var fixtureStore = app.Services.GetRequiredService<MockFixtureStore>();
var options = app.Services.GetRequiredService<IOptions<MockSuccessFactorsOptions>>().Value;
MockFixtureSummaryReporter.WriteSummary(Console.Out, fixtureStore.GetDocument(), "startup");

app.UseStaticFiles();
app.Use(async (httpContext, next) =>
{
    if (IsProtectedAdminPath(httpContext.Request.Path, options.Admin))
    {
        if (!options.Admin.Enabled)
        {
            httpContext.Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }

        if (options.Admin.RequireLoopback && !IsLoopbackRequest(httpContext.Request))
        {
            httpContext.Response.StatusCode = StatusCodes.Status403Forbidden;
            await httpContext.Response.WriteAsJsonAsync(new { error = "Mock admin endpoints are only available from loopback hosts." });
            return;
        }
    }

    await next();
});

app.MapGet("/healthz", () => TypedResults.Ok(new { status = "ok" }));

app.MapGet("/", () => options.Admin.Enabled
    ? Results.Redirect(options.Admin.Path)
    : Results.Redirect("/healthz"));

app.MapPost("/oauth/token", async Task<Results<Ok<TokenResponse>, UnauthorizedHttpResult>> (
    HttpContext httpContext,
    IOptions<MockSuccessFactorsOptions> configuredOptions,
    MockTokenService tokenService,
    CancellationToken cancellationToken) =>
{
    var form = await httpContext.Request.ReadFormAsync(cancellationToken);
    var configured = configuredOptions.Value.Authentication;
    var token = tokenService.IssueToken(
        grantType: form["grant_type"].ToString(),
        clientId: form["client_id"].ToString(),
        clientSecret: form["client_secret"].ToString(),
        companyId: form["company_id"].ToString(),
        configured: configured);

    if (token is null)
    {
        return TypedResults.Unauthorized();
    }

    return TypedResults.Ok(token);
});

app.MapGet("/odata/v2/$metadata", (IOptions<MockSuccessFactorsOptions> configuredOptions) =>
{
    var xml = MetadataDocument.Build(configuredOptions.Value.ServiceRoot);
    return Results.Content(xml, "application/xml");
});

app.MapGet("/odata/v2/PerPerson", (
    HttpContext httpContext,
    MockFixtureStore store,
    ODataResponseBuilder responseBuilder,
    IOptions<MockSuccessFactorsOptions> configuredOptions) =>
{
    if (!AuthenticationValidator.IsAuthorized(httpContext.Request, configuredOptions.Value.Authentication))
    {
        return Results.Unauthorized();
    }

    var query = ODataQueryParser.Parse(httpContext.Request.Query);
    if (!query.IsSupported)
    {
        return Results.BadRequest(new { error = query.ErrorMessage });
    }

    var workers = store.QueryWorkers("PerPerson", query);
    var worker = workers.Count == 1 ? workers[0] : null;
    if (worker?.Response?.ForceUnauthorized == true)
    {
        return Results.Unauthorized();
    }

    if (worker?.Response?.ForceMalformedPayload == true)
    {
        return Results.Content("not-json", "application/json");
    }

    if (worker?.Response?.ForceNotFound == true)
    {
        return Results.NotFound();
    }

    var payload = responseBuilder.Build(workers, query, "PerPerson", configuredOptions.Value.ServiceRoot);
    return Results.Json(payload);
});

app.MapGet("/odata/v2/EmpJob", (
    HttpContext httpContext,
    MockFixtureStore store,
    ODataResponseBuilder responseBuilder,
    IOptions<MockSuccessFactorsOptions> configuredOptions) =>
{
    if (!AuthenticationValidator.IsAuthorized(httpContext.Request, configuredOptions.Value.Authentication))
    {
        return Results.Unauthorized();
    }

    var query = ODataQueryParser.Parse(httpContext.Request.Query);
    if (!query.IsSupported)
    {
        return Results.BadRequest(new { error = query.ErrorMessage });
    }

    var workers = store.QueryWorkers("EmpJob", query);
    var worker = workers.Count == 1 ? workers[0] : null;
    if (worker?.Response?.ForceUnauthorized == true)
    {
        return Results.Unauthorized();
    }

    if (worker?.Response?.ForceMalformedPayload == true)
    {
        return Results.Content("not-json", "application/json");
    }

    if (worker?.Response?.ForceNotFound == true)
    {
        return Results.NotFound();
    }

    var payload = responseBuilder.Build(workers, query, "EmpJob", configuredOptions.Value.ServiceRoot);
    return Results.Json(payload);
});

var adminApi = app.MapGroup("/api/admin");

adminApi.MapGet("/workers", (string? filter, MockFixtureStore store, IOptions<MockSuccessFactorsOptions> configuredOptions) =>
    Results.Ok(store.GetAdminState(filter, configuredOptions.Value.Admin.Path)));

adminApi.MapGet("/workers/{workerId}", async (string workerId, MockFixtureStore store, MockPlannerComparisonService plannerComparisonService, CancellationToken cancellationToken) =>
{
    var worker = store.GetEditableWorker(workerId);
    return worker is null
        ? Results.NotFound(new { error = $"Worker '{workerId}' was not found." })
        : Results.Ok(new MockAdminWorkerDetailResponse(
            worker,
            Mode: "edit",
            BucketComparison: new MockAdminBucketComparison(
                MockBucket: BuildMockBucketSnapshot(store, workerId),
                PlannerBucket: await plannerComparisonService.CompareAsync(workerId, cancellationToken))));
});

adminApi.MapPost("/workers", (MockAdminWorkerUpsertRequest request, MockFixtureStore store) =>
    RunAdminMutation(() =>
    {
        var worker = store.CreateWorker(request);
        return Results.Ok(new MockAdminWorkerMutationResponse(
            Message: $"Created worker {worker.PersonIdExternal}.",
            Worker: store.GetEditableWorker(worker.PersonIdExternal)!));
    }));

adminApi.MapPut("/workers/{workerId}", (string workerId, MockAdminWorkerUpsertRequest request, MockFixtureStore store) =>
    RunAdminMutation(() =>
    {
        var worker = store.UpdateWorker(workerId, request);
        return Results.Ok(new MockAdminWorkerMutationResponse(
            Message: $"Saved worker {worker.PersonIdExternal}.",
            Worker: store.GetEditableWorker(worker.PersonIdExternal)!));
    }));

adminApi.MapPost("/workers/{workerId}/clone", (string workerId, MockAdminCloneRequest? request, MockFixtureStore store) =>
    RunAdminMutation(() =>
    {
        var sourceWorkerId = request?.SourceWorkerId ?? workerId;
        var worker = store.CloneWorker(sourceWorkerId);
        return Results.Ok(new MockAdminWorkerMutationResponse(
            Message: $"Cloned worker {sourceWorkerId} to {worker.PersonIdExternal}.",
            Worker: store.GetEditableWorker(worker.PersonIdExternal)!));
    }));

adminApi.MapPost("/workers/{workerId}/terminate", (string workerId, MockFixtureStore store) =>
    RunAdminMutation(() =>
    {
        var worker = store.TerminateWorker(workerId);
        return Results.Ok(new MockAdminWorkerMutationResponse(
            Message: $"Terminated worker {worker.PersonIdExternal}.",
            Worker: store.GetEditableWorker(worker.PersonIdExternal)!));
    }));

adminApi.MapPost("/workers/{workerId}/rehire", (string workerId, MockFixtureStore store) =>
    RunAdminMutation(() =>
    {
        var worker = store.RehireWorker(workerId);
        return Results.Ok(new MockAdminWorkerMutationResponse(
            Message: $"Rehired worker {worker.PersonIdExternal}.",
            Worker: store.GetEditableWorker(worker.PersonIdExternal)!));
    }));

adminApi.MapPost("/workers/{workerId}/lifecycle-state", (string workerId, MockAdminLifecycleStateRequest request, MockFixtureStore store) =>
    RunAdminMutation(() =>
    {
        var worker = store.ApplyLifecycleState(workerId, request.LifecycleState);
        return Results.Ok(new MockAdminWorkerMutationResponse(
            Message: $"Applied lifecycle state to worker {worker.PersonIdExternal}.",
            Worker: store.GetEditableWorker(worker.PersonIdExternal)!));
    }));

adminApi.MapDelete("/workers/{workerId}", (string workerId, MockFixtureStore store) =>
    RunAdminMutation(() =>
    {
        store.DeleteWorker(workerId);
        return Results.Ok(new { message = $"Deleted worker {workerId}." });
    }));

adminApi.MapPost("/reset", (MockFixtureStore store) =>
    RunAdminMutation(() =>
    {
        var count = store.ResetToSeed();
        return Results.Ok(new MockAdminResetResponse(
            Message: "Reset runtime fixtures to the seeded population.",
            WorkerCount: count));
    }));

app.MapRazorPages();
app.Run();

static IResult RunAdminMutation(Func<IResult> action)
{
    try
    {
        return action();
    }
    catch (KeyNotFoundException ex)
    {
        return Results.NotFound(new { error = ex.Message });
    }
    catch (InvalidOperationException ex)
    {
        return Results.BadRequest(new { error = ex.Message });
    }
}

static bool IsProtectedAdminPath(PathString path, MockAdminOptions adminOptions)
{
    return path.StartsWithSegments("/api/admin", StringComparison.OrdinalIgnoreCase) ||
           path.StartsWithSegments(adminOptions.Path, StringComparison.OrdinalIgnoreCase);
}

static MockAdminBucketSnapshot BuildMockBucketSnapshot(MockFixtureStore store, string workerId)
{
    var worker = store.GetDocument().Workers.FirstOrDefault(candidate =>
        string.Equals(candidate.PersonIdExternal, workerId, StringComparison.OrdinalIgnoreCase));
    var bucket = worker is null
        ? "unknown"
        : MockFixtureSummaryReporter.InferProvisioningBucket(worker);
    return new MockAdminBucketSnapshot(
        Bucket: bucket,
        Label: MockFixtureSummaryReporter.DescribeProvisioningBucket(bucket));
}

static bool IsLoopbackRequest(HttpRequest request)
{
    var remoteIp = request.HttpContext.Connection.RemoteIpAddress;
    if (remoteIp is not null && IPAddress.IsLoopback(remoteIp))
    {
        return true;
    }

    var host = request.Host.Host;
    if (string.IsNullOrWhiteSpace(host))
    {
        return false;
    }

    if (string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase))
    {
        return true;
    }

    if (IPAddress.TryParse(host.Trim('[', ']'), out var parsedAddress) && IPAddress.IsLoopback(parsedAddress))
    {
        return true;
    }

    return false;
}

public partial class Program
{
}

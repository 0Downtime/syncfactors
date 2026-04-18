using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.Extensions.Options;
using SyncFactors.MockSuccessFactors;

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

var app = builder.Build();
var fixtureStore = app.Services.GetRequiredService<MockFixtureStore>();
MockFixtureSummaryReporter.WriteSummary(Console.Out, fixtureStore.GetDocument(), "startup");

app.MapGet("/healthz", () => TypedResults.Ok(new { status = "ok" }));

app.MapPost("/oauth/token", async Task<Results<Ok<TokenResponse>, UnauthorizedHttpResult>> (
    HttpContext httpContext,
    IOptions<MockSuccessFactorsOptions> options,
    MockTokenService tokenService,
    CancellationToken cancellationToken) =>
{
    var form = await httpContext.Request.ReadFormAsync(cancellationToken);
    var configured = options.Value.Authentication;
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

app.MapGet("/odata/v2/$metadata", (IOptions<MockSuccessFactorsOptions> options) =>
{
    var xml = MetadataDocument.Build(options.Value.ServiceRoot);
    return Results.Content(xml, "application/xml");
});

app.MapGet("/odata/v2/PerPerson", (
    HttpContext httpContext,
    MockFixtureStore store,
    ODataResponseBuilder responseBuilder,
    IOptions<MockSuccessFactorsOptions> options) =>
{
    if (!AuthenticationValidator.IsAuthorized(httpContext.Request, options.Value.Authentication))
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

    var payload = responseBuilder.Build(workers, query, "PerPerson", options.Value.ServiceRoot);
    return Results.Json(payload);
});

app.MapGet("/odata/v2/EmpJob", (
    HttpContext httpContext,
    MockFixtureStore store,
    ODataResponseBuilder responseBuilder,
    IOptions<MockSuccessFactorsOptions> options) =>
{
    if (!AuthenticationValidator.IsAuthorized(httpContext.Request, options.Value.Authentication))
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

    var payload = responseBuilder.Build(workers, query, "EmpJob", options.Value.ServiceRoot);
    return Results.Json(payload);
});

app.Run();

public partial class Program
{
}

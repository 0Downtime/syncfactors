using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.Extensions.Logging;
using Serilog;
using SyncFactors.Api;
using SyncFactors.Contracts;
using SyncFactors.Domain;
using SyncFactors.Infrastructure;
using System.Net;
using System.Text.Json;

const string ViewerPolicy = "Viewer";
const string OperatorPolicy = "Operator";
const string AdminPolicy = "Admin";

var launcherProbeAction = LauncherProbe.GetRequestedAction(args);
if (!string.IsNullOrWhiteSpace(launcherProbeAction))
{
    if (!string.Equals(launcherProbeAction, LauncherProbe.BootstrapRequiredAction, StringComparison.OrdinalIgnoreCase))
    {
        throw new InvalidOperationException($"Unsupported launcher probe '{launcherProbeAction}'.");
    }

    var probeBuilder = WebApplication.CreateBuilder(args);
    var authOptions = probeBuilder.Configuration.GetSection("SyncFactors:Auth").Get<LocalAuthOptions>() ?? new LocalAuthOptions();
    var sqlitePathResolver = new SqlitePathResolver(probeBuilder.Configuration["SyncFactors:SqlitePath"]);
    var databaseInitializer = new SqliteDatabaseInitializer(sqlitePathResolver);
    var userStore = new SqliteLocalUserStore(sqlitePathResolver);

    await databaseInitializer.InitializeAsync(CancellationToken.None);
    var bootstrapRequired = await LauncherProbe.IsBootstrapRequiredAsync(authOptions, userStore, CancellationToken.None);
    Console.Out.WriteLine(bootstrapRequired ? "true" : "false");
    return;
}

var builder = WebApplication.CreateBuilder(args);
ConfigureLocalFileLogging(
    builder.Logging,
    processName: "api",
    enabledValue: builder.Configuration[LocalFileLogging.EnabledEnvironmentVariable],
    directoryValue: builder.Configuration[LocalFileLogging.DirectoryEnvironmentVariable]);
ConfigureApplicationInsights(builder);
var authSettings = builder.Configuration.GetSection("SyncFactors:Auth").Get<LocalAuthOptions>() ?? new LocalAuthOptions();
var cspEnabled = builder.Configuration.GetValue<bool?>("SyncFactors:SecurityHeaders:EnableContentSecurityPolicy")
    ?? !builder.Environment.IsDevelopment();
var oidcEnabled = IsOidcEnabled(authSettings);
var realtimeEnabled = builder.Configuration.GetValue<bool?>("SyncFactors:Realtime:Enabled") ?? true;
var dashboardOptions = new DashboardOptions(
    builder.Configuration.GetValue<bool?>("SyncFactors:Dashboard:HealthProbes:Enabled") ?? true,
    builder.Configuration.GetValue<int?>("SyncFactors:Dashboard:HealthProbes:IntervalSeconds") ?? 45);

ValidateHttpsOnlyBindings(builder.Configuration);

builder.Services.AddSingleton(new SqlitePathResolver(builder.Configuration["SyncFactors:SqlitePath"]));
builder.Services.AddSingleton(new SyncFactorsConfigPathResolver(
    builder.Configuration["SyncFactors:ConfigPath"],
    builder.Configuration["SyncFactors:MappingConfigPath"]));
builder.Services.AddSingleton(new ScaffoldDataPathResolver(builder.Configuration["SyncFactors:ScaffoldDataPath"]));
builder.Services.AddSingleton<AdminConfigurationSnapshotBuilder>();
builder.Services.AddSingleton(dashboardOptions);
builder.Services.AddSingleton<DashboardSettingsProvider>();
builder.Services.Configure<LocalAuthOptions>(builder.Configuration.GetSection("SyncFactors:Auth"));
builder.Services.Configure<ForwardedHeadersOptions>(options =>
{
    options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
    options.KnownIPNetworks.Clear();
    options.KnownProxies.Clear();
});
builder.Services.AddSingleton<SqliteDatabaseInitializer>();
builder.Services.AddSingleton<SyncFactorsConfigurationLoader>();
builder.Services.AddSingleton<SyncFactorsConfigurationValidator>();
builder.Services.AddSingleton<IEmailAddressPolicy, ConfiguredEmailAddressPolicy>();
builder.Services.AddSingleton<ISecurityAuditService, SecurityAuditService>();
builder.Services.AddSingleton<ILocalUserStore, SqliteLocalUserStore>();
builder.Services.AddSingleton<ILocalAuthService, LocalAuthService>();
builder.Services.AddSingleton<Microsoft.AspNetCore.Identity.IPasswordHasher<LocalUserRecord>, Microsoft.AspNetCore.Identity.PasswordHasher<LocalUserRecord>>();
builder.Services.AddSingleton<ScaffoldDataStore>();
builder.Services.AddSingleton<ScaffoldWorkerSource>();
builder.Services.AddSingleton(serviceProvider =>
{
    var config = serviceProvider.GetRequiredService<SyncFactorsConfigurationLoader>().GetSyncConfig();
    return new WorkerRunSettings(config.Safety.MaxCreatesPerRun, config.Safety.MaxDisablesPerRun, config.Safety.MaxDeletionsPerRun);
});
builder.Services.AddSingleton(serviceProvider =>
{
    var config = serviceProvider.GetRequiredService<SyncFactorsConfigurationLoader>().GetSyncConfig();
    return new RealSyncSettings(config.Sync.RealSyncEnabled);
});
builder.Services.AddSingleton(serviceProvider =>
{
    var config = serviceProvider.GetRequiredService<SyncFactorsConfigurationLoader>().GetSyncConfig();
    return new GraveyardDeletionQueueSettings(
        RetentionDays: config.Sync.DeletionRetentionDays,
        AutoDeleteEnabled: config.Sync.AutoDeleteFromGraveyard);
});
builder.Services.AddSingleton(serviceProvider =>
{
    var config = serviceProvider.GetRequiredService<SyncFactorsConfigurationLoader>().GetSyncConfig();
    return new LifecyclePolicySettings(
        config.Ad.DefaultActiveOu,
        config.Ad.PrehireOu,
        config.Ad.GraveyardOu,
        config.SuccessFactors.Query.InactiveStatusField,
        config.SuccessFactors.Query.InactiveStatusValues,
        config.Ad.LeaveOu,
        config.Sync.LeaveStatusValues,
        config.Ad.IdentityAttribute);
});
builder.Services.AddSingleton<ScaffoldDirectoryGateway>();
builder.Services.AddSingleton<ScaffoldDirectoryCommandGateway>();
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<IWorkerPreviewLogWriter, FileWorkerPreviewLogWriter>();
builder.Services.AddSingleton<IDeltaSyncStateStore, SqliteDeltaSyncStateStore>();
builder.Services.AddSingleton<IDeltaSyncService, SuccessFactorsDeltaSyncService>();
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
builder.Services.AddTransient<ActiveDirectoryGateway>();
builder.Services.AddTransient<IDirectoryGateway>(serviceProvider => serviceProvider.GetRequiredService<ActiveDirectoryGateway>());
builder.Services.AddTransient<ActiveDirectoryCommandGateway>();
builder.Services.AddTransient<IDirectoryCommandGateway>(serviceProvider => serviceProvider.GetRequiredService<ActiveDirectoryCommandGateway>());
builder.Services.AddSingleton<IAttributeMappingProvider, AttributeMappingProvider>();
builder.Services.AddSingleton<IIdentityMatcher, IdentityMatcher>();
builder.Services.AddSingleton<ILifecyclePolicy, LifecyclePolicy>();
builder.Services.AddSingleton<IAttributeDiffService, AttributeDiffService>();
builder.Services.AddSingleton<IActiveDirectoryConnectionPool, ActiveDirectoryConnectionPool>();
builder.Services.AddSingleton<IWorkerHeartbeatStore, SqliteWorkerHeartbeatStore>();
builder.Services.AddTransient<IWorkerPreviewPlanner, WorkerPreviewPlanner>();
builder.Services.AddTransient<IApplyPreviewService, ApplyPreviewService>();
builder.Services.AddTransient<IFullSyncRunService, FullSyncRunService>();
builder.Services.AddSingleton<IDashboardSnapshotService, DashboardSnapshotService>();
builder.Services.AddSingleton<IRuntimeStatusStore, SqliteRuntimeStatusStore>();
builder.Services.AddSingleton<IRunRepository, SqliteRunRepository>();
builder.Services.AddSingleton<IGraveyardRetentionStore, SqliteGraveyardRetentionStore>();
builder.Services.AddTransient<RunEntriesQueryService>();
builder.Services.AddTransient<GraveyardDeletionQueueService>();
builder.Services.AddSingleton<IRunQueueStore, SqliteRunQueueStore>();
builder.Services.AddSingleton<RunQueueRecoveryService>();
builder.Services.AddSingleton<ISyncScheduleStore, SqliteSyncScheduleStore>();
builder.Services.AddSingleton<IDashboardSettingsStore, SqliteDashboardSettingsStore>();
builder.Services.AddSingleton<DashboardRealtimeConnectionTracker>();
builder.Services.AddTransient<IWorkerPlanningService, WorkerPlanningService>();
builder.Services.AddSingleton<IDirectoryMutationCommandBuilder, DirectoryMutationCommandBuilder>();
builder.Services.AddSignalR()
    .AddJsonProtocol(options =>
    {
        options.PayloadSerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
        options.PayloadSerializerOptions.DictionaryKeyPolicy = JsonNamingPolicy.CamelCase;
    });

if (realtimeEnabled)
{
    builder.Services.AddHostedService<DashboardRealtimeService>();
}

var authenticationBuilder = builder.Services.AddAuthentication(options =>
{
    options.DefaultScheme = CookieAuthenticationDefaults.AuthenticationScheme;
    options.DefaultAuthenticateScheme = CookieAuthenticationDefaults.AuthenticationScheme;
    options.DefaultSignInScheme = CookieAuthenticationDefaults.AuthenticationScheme;
    options.DefaultChallengeScheme = oidcEnabled
        ? OpenIdConnectDefaults.AuthenticationScheme
        : CookieAuthenticationDefaults.AuthenticationScheme;
})
    .AddCookie(options =>
    {
        options.LoginPath = "/Login";
        options.AccessDeniedPath = "/AccessDenied";
        options.Cookie.Name = "SyncFactors.Auth";
        options.Cookie.HttpOnly = true;
        options.Cookie.SameSite = SameSiteMode.Lax;
        options.Cookie.SecurePolicy = builder.Environment.IsDevelopment()
            ? CookieSecurePolicy.SameAsRequest
            : CookieSecurePolicy.Always;
        options.SlidingExpiration = true;
        options.ExpireTimeSpan = authSettings.GetIdleTimeout();
        options.Events = new CookieAuthenticationEvents
        {
            OnRedirectToLogin = context => HandleAuthRedirectAsync(context, StatusCodes.Status401Unauthorized),
            OnRedirectToAccessDenied = context => HandleAuthRedirectAsync(context, StatusCodes.Status403Forbidden),
            OnValidatePrincipal = context => ValidateCookiePrincipalAsync(context, authSettings)
        };
    });

if (oidcEnabled)
{
    authenticationBuilder.AddOpenIdConnect(OpenIdConnectDefaults.AuthenticationScheme, options =>
    {
        options.SignInScheme = CookieAuthenticationDefaults.AuthenticationScheme;
        options.Authority = authSettings.Oidc.Authority;
        options.ClientId = authSettings.Oidc.ClientId;
        options.ClientSecret = authSettings.Oidc.ClientSecret;
        options.CallbackPath = authSettings.Oidc.CallbackPath;
        options.SignedOutCallbackPath = authSettings.Oidc.SignedOutCallbackPath;
        options.SignedOutRedirectUri = "/Login?LoggedOut=true";
        options.ResponseType = "code";
        options.SaveTokens = true;
        options.GetClaimsFromUserInfoEndpoint = true;
        options.MapInboundClaims = false;
        options.Scope.Clear();
        options.Scope.Add("openid");
        options.Scope.Add("profile");
        options.Scope.Add("email");
        options.TokenValidationParameters.NameClaimType = authSettings.Oidc.DisplayNameClaimType;
        options.Events = new OpenIdConnectEvents
        {
            OnTokenValidated = context =>
            {
                if (context.Principal?.Identity is ClaimsIdentity identity)
                {
                    ApplyOidcIdentity(identity, authSettings);
                }

                return Task.CompletedTask;
            },
            OnRedirectToIdentityProviderForSignOut = context =>
            {
                var idToken = context.Properties?.GetTokenValue("id_token");
                if (!string.IsNullOrWhiteSpace(idToken))
                {
                    context.ProtocolMessage.IdTokenHint = idToken;
                }

                return Task.CompletedTask;
            }
        };
    });
}

builder.Services.AddAuthorization(options =>
{
    options.AddPolicy(ViewerPolicy, policy => policy.RequireRole(
        SecurityRoles.Viewer,
        SecurityRoles.Operator,
        SecurityRoles.Admin,
        SecurityRoles.BreakGlassAdmin));
    options.AddPolicy(OperatorPolicy, policy => policy.RequireRole(
        SecurityRoles.Operator,
        SecurityRoles.Admin,
        SecurityRoles.BreakGlassAdmin));
    options.AddPolicy(AdminPolicy, policy => policy.RequireRole(
        SecurityRoles.Admin,
        SecurityRoles.BreakGlassAdmin));
});

builder.Services.AddRazorPages(options =>
{
    options.Conventions.AuthorizeFolder("/", ViewerPolicy);
    options.Conventions.AuthorizePage("/Sync", OperatorPolicy);
    options.Conventions.AuthorizePage("/Preview", OperatorPolicy);
    options.Conventions.AuthorizeFolder("/Admin", AdminPolicy);
    options.Conventions.AllowAnonymousToPage("/AccessDenied");
    options.Conventions.AllowAnonymousToPage("/Login");
});

var app = builder.Build();

await app.Services.GetRequiredService<SqliteDatabaseInitializer>().InitializeAsync(CancellationToken.None);
await app.Services.GetRequiredService<ILocalAuthService>().EnsureBootstrapAdminAsync(CancellationToken.None);
app.Services.GetRequiredService<SyncFactorsConfigurationValidator>().Validate();
ValidateAuthConfiguration(app);
LogConfiguredEndpoints(app);

app.UseForwardedHeaders();
if (!app.Environment.IsDevelopment())
{
    app.UseHsts();
}

app.UseHttpsRedirection();
app.Use(async (context, next) =>
{
    if (cspEnabled)
    {
        context.Response.Headers["Content-Security-Policy"] =
            "default-src 'self'; style-src 'self' 'unsafe-inline'; img-src 'self' data:; script-src 'self'; connect-src 'self' ws: wss:; frame-ancestors 'none'; base-uri 'self'";
        context.Response.Headers["X-SyncFactors-Csp-Version"] = "2";
    }

    context.Response.Headers["X-Frame-Options"] = "DENY";
    context.Response.Headers["X-Content-Type-Options"] = "nosniff";
    context.Response.Headers["Referrer-Policy"] = "strict-origin-when-cross-origin";
    await next();
});

app.UseStaticFiles();
app.UseAuthentication();
app.UseAuthorization();

var api = app.MapGroup("/api");
var sessionApi = app.MapGroup("/api/session");

sessionApi.MapGet(string.Empty, async (HttpContext httpContext, ILocalAuthService authService, CancellationToken cancellationToken) =>
{
    var session = await LocalSessionManager.BuildSessionResponseAsync(httpContext, authService, cancellationToken);
    return Results.Ok(session);
}).AllowAnonymous();

sessionApi.MapPost("/login", async (
    SessionLoginRequest request,
    HttpContext httpContext,
    ILocalAuthService authService,
    Microsoft.Extensions.Options.IOptions<LocalAuthOptions> authOptions,
    CancellationToken cancellationToken) =>
{
    if (!authService.IsLocalAuthenticationEnabled)
    {
        return Results.BadRequest(new { error = "Local sign-in is disabled for this environment." });
    }

    var result = await authService.AuthenticateAsync(request.Username, request.Password, cancellationToken);
    if (!result.Succeeded || result.User is null)
    {
        return Results.BadRequest(new { error = "Invalid username or password." });
    }

    await LocalSessionManager.SignInAsync(httpContext, result.User, request.RememberMe, authOptions.Value);
    await authService.RecordSuccessfulLoginAsync(result.User.UserId, cancellationToken);
    var session = await LocalSessionManager.BuildSessionResponseAsync(httpContext, authService, cancellationToken);
    return Results.Ok(session);
}).AllowAnonymous();

sessionApi.MapPost("/logout", async (HttpContext httpContext) =>
{
    await LocalSessionManager.SignOutAsync(httpContext);
    return Results.Ok(LocalSessionManager.AnonymousSession);
}).AllowAnonymous();

app.MapPublicHealthEndpoints();

var readApi = api.MapGroup(string.Empty)
    .RequireAuthorization(ViewerPolicy);
var operatorApi = api.MapGroup(string.Empty)
    .RequireAuthorization(OperatorPolicy);
var adminApi = api.MapGroup(string.Empty)
    .RequireAuthorization(AdminPolicy);

readApi.MapGet("/status", async (IRuntimeStatusStore store, CancellationToken cancellationToken) =>
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

readApi.MapGet("/dashboard", async (IDashboardSnapshotService dashboardSnapshotService, CancellationToken cancellationToken) =>
{
    var snapshot = await dashboardSnapshotService.GetSnapshotAsync(cancellationToken);
    return Results.Ok(snapshot);
});

readApi.MapGet("/dashboard/health", HealthEndpointMappings.GetDashboardHealthAsync);

readApi.MapGet("/runs", async (int? page, int? pageSize, IRunRepository repository, CancellationToken cancellationToken) =>
{
    var runs = await repository.ListRunsAsync(cancellationToken);
    var resolvedPageSize = Math.Clamp(pageSize ?? runs.Count, 1, 200);
    var total = runs.Count;
    var totalPages = Math.Max(1, (int)Math.Ceiling(total / (double)resolvedPageSize));
    var resolvedPage = Math.Clamp(page ?? 1, 1, totalPages);
    var pagedRuns = runs
        .Skip((resolvedPage - 1) * resolvedPageSize)
        .Take(resolvedPageSize)
        .ToArray();
    return Results.Ok(new
    {
        runs = pagedRuns,
        total,
        page = resolvedPage,
        pageSize = resolvedPageSize
    });
});

readApi.MapGet("/runs/queue", async (IRunQueueStore queueStore, CancellationToken cancellationToken) =>
{
    var request = await queueStore.GetPendingOrActiveAsync(cancellationToken);
    return Results.Ok(new { request });
});

operatorApi.MapPost("/runs", async (StartRunRequest request, ClaimsPrincipal user, IRunQueueStore queueStore, ISecurityAuditService audit, CancellationToken cancellationToken) =>
{
    if (await queueStore.HasPendingOrActiveRunAsync(cancellationToken))
    {
        return Results.Conflict(new { error = "A run is already pending or in progress." });
    }

    var requestedBy = ResolveRequestedBy(user, request.RequestedBy ?? "API");
    var queued = await queueStore.EnqueueAsync(
        request with { RequestedBy = requestedBy },
        cancellationToken);
    audit.Write("RunQueued", "Success", ("RequestedBy", requestedBy), ("Mode", queued.Mode), ("DryRun", queued.DryRun));
    return Results.Accepted($"/api/runs/{queued.RequestId}", queued);
});

operatorApi.MapPost("/runs/cancel", async (ClaimsPrincipal user, IRunQueueStore queueStore, ISecurityAuditService audit, CancellationToken cancellationToken) =>
{
    var requestedBy = ResolveRequestedBy(user, "API");
    var canceled = await queueStore.CancelPendingOrActiveAsync(requestedBy, cancellationToken);
    if (canceled)
    {
        audit.Write("RunCancelled", "Success", ("RequestedBy", requestedBy));
    }

    return canceled
        ? Results.Ok(new { status = "CancellationRequested" })
        : Results.NotFound(new { error = "No queued or active run was available to cancel." });
});

readApi.MapGet("/sync/schedule", async (ISyncScheduleStore scheduleStore, CancellationToken cancellationToken) =>
{
    var schedule = await scheduleStore.GetCurrentAsync(cancellationToken);
    return Results.Ok(new { schedule });
});

adminApi.MapPut("/sync/schedule", async (UpdateSyncScheduleRequest request, ClaimsPrincipal user, ISyncScheduleStore scheduleStore, ISecurityAuditService audit, CancellationToken cancellationToken) =>
{
    var schedule = await scheduleStore.UpdateAsync(request, cancellationToken);
    audit.Write("SyncScheduleUpdated", "Success", ("RequestedBy", ResolveRequestedBy(user, "API")), ("Enabled", schedule.Enabled), ("IntervalMinutes", schedule.IntervalMinutes));
    return Results.Ok(new { schedule });
});

readApi.MapGet("/runs/{runId}", async (string runId, IRunRepository repository, CancellationToken cancellationToken) =>
{
    var run = await repository.GetRunAsync(runId, cancellationToken);
    return run is null ? Results.NotFound() : Results.Ok(run);
});

readApi.MapGet("/previews/{runId}", async (string runId, IRunRepository repository, CancellationToken cancellationToken) =>
{
    var preview = await repository.GetWorkerPreviewAsync(runId, cancellationToken);
    return preview is null ? Results.NotFound() : Results.Ok(preview);
});

operatorApi.MapPost("/previews", async (
    CreatePreviewRequest request,
    IWorkerPreviewPlanner previewPlanner,
    CancellationToken cancellationToken) =>
{
    if (string.IsNullOrWhiteSpace(request.WorkerId))
    {
        return Results.BadRequest(new { error = "Worker ID is required." });
    }

    try
    {
        var preview = await previewPlanner.PreviewAsync(request.WorkerId, cancellationToken);
        return Results.Ok(preview);
    }
    catch (Exception ex)
    {
        return Results.BadRequest(new { error = ex.Message });
    }
});

readApi.MapGet("/workers/{workerId}/previews", async (string workerId, int? take, IRunRepository repository, CancellationToken cancellationToken) =>
{
    var history = await repository.ListWorkerPreviewHistoryAsync(workerId, take ?? 6, cancellationToken);
    return Results.Ok(new { workerId, previews = history });
});

operatorApi.MapGet("/workers/{workerId}/preview", async (string workerId, IWorkerPreviewPlanner previewPlanner, CancellationToken cancellationToken) =>
{
    var preview = await previewPlanner.PreviewAsync(workerId, cancellationToken);
    return Results.Ok(preview);
});

readApi.MapGet("/runs/{runId}/entries", async (
    string runId,
    string? bucket,
    string? workerId,
    string? reason,
    string? filter,
    string? employmentStatus,
    string? entryId,
    int? page,
    int? pageSize,
    RunEntriesQueryService queryService,
    CancellationToken cancellationToken) =>
{
    var result = await queryService.LoadAsync(runId, bucket, workerId, reason, filter, employmentStatus, entryId, page, pageSize, cancellationToken);
    if (result is null)
    {
        return Results.NotFound();
    }

    return Results.Ok(new
    {
        run = result.Run.Run,
        entries = result.Entries,
        attributeTotals = result.AttributeTotals,
        total = result.Total,
        page = result.Page,
        pageSize = result.PageSize
    });
});

operatorApi.MapPost("/preview/{workerId}/apply", async (
    string workerId,
    ApplyPreviewRequest request,
    ClaimsPrincipal user,
    IApplyPreviewService applyPreviewService,
    ISecurityAuditService audit,
    CancellationToken cancellationToken) =>
{
    if (!string.Equals(workerId, request.WorkerId, StringComparison.Ordinal))
    {
        return Results.BadRequest(new { error = "Route worker id does not match the apply request." });
    }

    var result = await applyPreviewService.ApplyAsync(request, cancellationToken);
    audit.Write("PreviewApplied", result.Succeeded ? "Success" : "Failure", ("RequestedBy", ResolveRequestedBy(user, "API")), ("WorkerId", request.WorkerId), ("PreviewRunId", request.PreviewRunId));
    return result.Succeeded
        ? Results.Ok(result)
        : Results.BadRequest(result);
});

operatorApi.MapPost("/runs/full", async (
    LaunchFullRunRequest request,
    ClaimsPrincipal user,
    IFullSyncRunService fullSyncRunService,
    ISecurityAuditService audit,
    CancellationToken cancellationToken) =>
{
    var result = await fullSyncRunService.LaunchAsync(request, cancellationToken);
    audit.Write("FullRunLaunched", string.Equals(result.Status, "Succeeded", StringComparison.OrdinalIgnoreCase) ? "Success" : "Failure", ("RequestedBy", ResolveRequestedBy(user, "API")), ("DryRun", request.DryRun), ("RunId", result.RunId));
    return string.Equals(result.Status, "Succeeded", StringComparison.OrdinalIgnoreCase)
        ? Results.Ok(result)
        : Results.BadRequest(result);
});

operatorApi.MapPost("/runs/delete-all", async (
    DeleteAllUsersRequest request,
    ClaimsPrincipal user,
    IRunQueueStore queueStore,
    RealSyncSettings realSyncSettings,
    ISecurityAuditService audit,
    CancellationToken cancellationToken) =>
{
    if (!realSyncSettings.Enabled)
    {
        return Results.BadRequest(new { error = "Real AD sync is disabled for this environment." });
    }

    if (await queueStore.HasPendingOrActiveRunAsync(cancellationToken))
    {
        return Results.Conflict(new { error = "A run is already pending or in progress." });
    }

    if (!string.Equals(request.ConfirmationText?.Trim(), SyncFactors.Api.Pages.SyncModel.DeleteAllUsersConfirmationPhrase, StringComparison.Ordinal))
    {
        return Results.BadRequest(new { error = $"Type {SyncFactors.Api.Pages.SyncModel.DeleteAllUsersConfirmationPhrase} to queue the delete-all AD reset run." });
    }

    var queued = await queueStore.EnqueueAsync(
        new StartRunRequest(
            DryRun: false,
            Mode: "DeleteAllUsers",
            RunTrigger: "DeleteAllUsers",
            RequestedBy: ResolveRequestedBy(user, "API")),
        cancellationToken);

    audit.Write("DeleteAllUsersQueued", "Success", ("RequestedBy", ResolveRequestedBy(user, "API")));
    return Results.Accepted($"/api/runs/{queued.RequestId}", queued);
});

adminApi.MapGet("/admin/users", async (ILocalAuthService authService, CancellationToken cancellationToken) =>
{
    var users = await authService.ListUsersAsync(cancellationToken);
    return Results.Ok(new { users });
});

adminApi.MapPost("/admin/users", async (
    CreateLocalUserRequest request,
    ClaimsPrincipal user,
    ILocalAuthService authService,
    ISecurityAuditService audit,
    CancellationToken cancellationToken) =>
{
    var result = await authService.CreateUserAsync(request.Username, request.Password, request.IsAdmin, cancellationToken);
    audit.Write(
        "LocalUserCreated",
        result.Succeeded ? "Success" : "Failure",
        ("RequestedBy", ResolveRequestedBy(user, "API")),
        ("Username", request.Username),
        ("Role", request.IsAdmin ? SecurityRoles.Admin : SecurityRoles.Viewer));
    return result.Succeeded ? Results.Ok(result) : Results.BadRequest(result);
});

adminApi.MapPost("/admin/users/{userId}/password", async (
    string userId,
    ResetLocalUserPasswordRequest request,
    ClaimsPrincipal user,
    ILocalAuthService authService,
    ISecurityAuditService audit,
    CancellationToken cancellationToken) =>
{
    var result = await authService.ResetPasswordAsync(userId, request.NewPassword, cancellationToken);
    audit.Write("LocalUserPasswordReset", result.Succeeded ? "Success" : "Failure", ("RequestedBy", ResolveRequestedBy(user, "API")), ("UserId", userId));
    return result.Succeeded ? Results.Ok(result) : Results.BadRequest(result);
});

adminApi.MapPost("/admin/users/{userId}/role", async (
    string userId,
    SetLocalUserRoleRequest request,
    ClaimsPrincipal user,
    ILocalAuthService authService,
    ISecurityAuditService audit,
    CancellationToken cancellationToken) =>
{
    var actingUserId = user.FindFirstValue(ClaimTypes.NameIdentifier) ?? string.Empty;
    var result = await authService.SetUserRoleAsync(userId, request.IsAdmin, actingUserId, cancellationToken);
    audit.Write(
        "LocalUserRoleUpdated",
        result.Succeeded ? "Success" : "Failure",
        ("RequestedBy", ResolveRequestedBy(user, "API")),
        ("UserId", userId),
        ("Role", request.IsAdmin ? SecurityRoles.Admin : SecurityRoles.Viewer));
    return result.Succeeded ? Results.Ok(result) : Results.BadRequest(result);
});

adminApi.MapPost("/admin/users/{userId}/active", async (
    string userId,
    SetLocalUserActiveStateRequest request,
    ClaimsPrincipal user,
    ILocalAuthService authService,
    ISecurityAuditService audit,
    CancellationToken cancellationToken) =>
{
    var actingUserId = user.FindFirstValue(ClaimTypes.NameIdentifier) ?? string.Empty;
    var result = await authService.SetUserActiveStateAsync(userId, request.IsActive, actingUserId, cancellationToken);
    audit.Write(
        "LocalUserActiveStateUpdated",
        result.Succeeded ? "Success" : "Failure",
        ("RequestedBy", ResolveRequestedBy(user, "API")),
        ("UserId", userId),
        ("IsActive", request.IsActive));
    return result.Succeeded ? Results.Ok(result) : Results.BadRequest(result);
});

adminApi.MapDelete("/admin/users/{userId}", async (
    string userId,
    ClaimsPrincipal user,
    ILocalAuthService authService,
    ISecurityAuditService audit,
    CancellationToken cancellationToken) =>
{
    var actingUserId = user.FindFirstValue(ClaimTypes.NameIdentifier) ?? string.Empty;
    var result = await authService.DeleteUserAsync(userId, actingUserId, cancellationToken);
    audit.Write("LocalUserDeleted", result.Succeeded ? "Success" : "Failure", ("RequestedBy", ResolveRequestedBy(user, "API")), ("UserId", userId));
    return result.Succeeded ? Results.Ok(result) : Results.BadRequest(result);
});

app.MapHub<DashboardHub>("/hubs/dashboard")
    .RequireAuthorization(ViewerPolicy);

app.MapRazorPages();

app.Run();

static void ValidateAuthConfiguration(WebApplication app)
{
    var authOptions = app.Services.GetRequiredService<Microsoft.Extensions.Options.IOptions<LocalAuthOptions>>().Value;
    var oidcEnabled = IsOidcEnabled(authOptions);
    var mode = authOptions.Mode ?? "local-break-glass";

    if (string.Equals(mode, "oidc", StringComparison.OrdinalIgnoreCase) && !oidcEnabled)
    {
        throw new InvalidOperationException("SyncFactors:Auth mode 'oidc' requires OIDC authority and client ID.");
    }

    if (authOptions.IdleTimeoutMinutes is < LocalAuthOptions.MinIdleTimeoutMinutes or > LocalAuthOptions.MaxIdleTimeoutMinutes)
    {
        throw new InvalidOperationException($"SyncFactors:Auth:IdleTimeoutMinutes must be between {LocalAuthOptions.MinIdleTimeoutMinutes} and {LocalAuthOptions.MaxIdleTimeoutMinutes}.");
    }

    if (authOptions.AbsoluteSessionHours is < LocalAuthOptions.MinAbsoluteSessionHours or > LocalAuthOptions.MaxAbsoluteSessionHours)
    {
        throw new InvalidOperationException($"SyncFactors:Auth:AbsoluteSessionHours must be between {LocalAuthOptions.MinAbsoluteSessionHours} and {LocalAuthOptions.MaxAbsoluteSessionHours}.");
    }

    if (authOptions.RememberMeSessionHours is < LocalAuthOptions.MinRememberMeSessionHours or > LocalAuthOptions.MaxRememberMeSessionHours)
    {
        throw new InvalidOperationException($"SyncFactors:Auth:RememberMeSessionHours must be between {LocalAuthOptions.MinRememberMeSessionHours} and {LocalAuthOptions.MaxRememberMeSessionHours}.");
    }
}

static bool IsOidcEnabled(LocalAuthOptions options) =>
    (string.Equals(options.Mode, "oidc", StringComparison.OrdinalIgnoreCase) ||
     string.Equals(options.Mode, "hybrid", StringComparison.OrdinalIgnoreCase)) &&
    !string.IsNullOrWhiteSpace(options.Oidc.Authority) &&
    !string.IsNullOrWhiteSpace(options.Oidc.ClientId);

static void ValidateHttpsOnlyBindings(ConfigurationManager configuration)
{
    var urls = configuration["urls"] ?? Environment.GetEnvironmentVariable("ASPNETCORE_URLS");
    if (string.IsNullOrWhiteSpace(urls))
    {
        return;
    }

    var configuredUrls = urls
        .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .Select(url => new Uri(url, UriKind.Absolute))
        .ToArray();

    if (configuredUrls.Any(uri => string.Equals(uri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase)))
    {
        throw new InvalidOperationException("SyncFactors.Api is configured for HTTPS-only operation and will not bind HTTP endpoints.");
    }
}

static void LogConfiguredEndpoints(WebApplication app)
{
    var config = app.Services.GetRequiredService<SyncFactorsConfigurationLoader>().GetSyncConfig();
    var authOptions = app.Services.GetRequiredService<Microsoft.Extensions.Options.IOptions<LocalAuthOptions>>().Value;
    var activeDirectoryPort = ResolveActiveDirectoryPort(config.Ad);
    var usesGlobalCatalog = activeDirectoryPort is 3268 or 3269;
    app.Logger.LogInformation(
        "[AD-TRANSPORT] Active Directory startup transport: {ActiveDirectoryStartupTransport}.",
        ActiveDirectoryTransportModeFormatter.DescribeStartupTransport(config.Ad.Transport.Mode));
    app.Logger.LogWarning("========== AD ENDPOINT DIAGNOSTIC ==========");
    app.Logger.LogWarning(
        "[AD-ENDPOINT] ActiveDirectoryServer={ActiveDirectoryServer} ActiveDirectoryPort={ActiveDirectoryPort} ActiveDirectoryAccount={ActiveDirectoryAccount} ActiveDirectorySimpleBindPrincipalFormat={ActiveDirectorySimpleBindPrincipalFormat} ActiveDirectoryTransport={ActiveDirectoryTransport} ActiveDirectoryUsesGlobalCatalog={ActiveDirectoryUsesGlobalCatalog} SuccessFactorsBaseUrl={SuccessFactorsBaseUrl} SuccessFactorsAccount={SuccessFactorsAccount} AuthMode={AuthMode}",
        config.Ad.Server,
        activeDirectoryPort,
        string.IsNullOrWhiteSpace(config.Ad.Username) ? "anonymous" : config.Ad.Username,
        DescribeSimpleBindPrincipalFormat(config.Ad.Username),
        config.Ad.Transport.Mode,
        usesGlobalCatalog,
        config.SuccessFactors.BaseUrl,
        DescribeSuccessFactorsAccount(config.SuccessFactors.Auth),
        authOptions.Mode);
    app.Logger.LogWarning("============================================");

    if (usesGlobalCatalog)
    {
        app.Logger.LogCritical(
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

static Task ValidateCookiePrincipalAsync(Microsoft.AspNetCore.Authentication.Cookies.CookieValidatePrincipalContext context, LocalAuthOptions authSettings)
{
    var identity = context.Principal?.Identity as ClaimsIdentity;
    if (identity is null)
    {
        return Task.CompletedTask;
    }

    var issuedAtValue = identity.FindFirst(SecurityClaimTypes.SessionIssuedAt)?.Value;
    if (!DateTimeOffset.TryParse(issuedAtValue, out var issuedAt) ||
        DateTimeOffset.UtcNow - issuedAt > authSettings.GetAbsoluteSessionLifetime())
    {
        context.RejectPrincipal();
        return context.HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
    }

    var authSource = identity.FindFirst(SecurityClaimTypes.AuthSource)?.Value;
    if (!string.Equals(authSource, "local", StringComparison.Ordinal))
    {
        return Task.CompletedTask;
    }

    var userId = context.Principal?.FindFirst(ClaimTypes.NameIdentifier)?.Value;
    if (string.IsNullOrWhiteSpace(userId))
    {
        context.RejectPrincipal();
        return context.HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
    }

    return RefreshLocalPrincipalAsync(context, userId);
}

static async Task RefreshLocalPrincipalAsync(Microsoft.AspNetCore.Authentication.Cookies.CookieValidatePrincipalContext context, string userId)
{
    var authService = context.HttpContext.RequestServices.GetRequiredService<ILocalAuthService>();
    var currentUser = await authService.FindUserByIdAsync(userId, context.HttpContext.RequestAborted);
    if (currentUser is null || !currentUser.IsActive)
    {
        context.RejectPrincipal();
        await context.HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
        return;
    }

    if (currentUser.LockoutEndAt is not null && currentUser.LockoutEndAt > DateTimeOffset.UtcNow)
    {
        context.RejectPrincipal();
        await context.HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
        return;
    }

    if (context.Principal?.Identity is not ClaimsIdentity identity)
    {
        return;
    }

    ReplaceClaim(identity, ClaimTypes.Name, currentUser.Username);
    ReplaceClaim(identity, ClaimTypes.Role, currentUser.Role);
}

static void ApplyOidcIdentity(ClaimsIdentity identity, LocalAuthOptions authSettings)
{
    ReplaceClaim(identity, SecurityClaimTypes.AuthSource, "oidc");
    ReplaceClaim(identity, SecurityClaimTypes.SessionIssuedAt, DateTimeOffset.UtcNow.ToString("O"));

    var preferredUsername = identity.FindFirst(authSettings.Oidc.UsernameClaimType)?.Value
        ?? identity.FindFirst(authSettings.Oidc.DisplayNameClaimType)?.Value
        ?? identity.FindFirst("sub")?.Value
        ?? "oidc-user";
    ReplaceClaim(identity, ClaimTypes.Name, preferredUsername);

    var subject = identity.FindFirst("sub")?.Value;
    if (!string.IsNullOrWhiteSpace(subject))
    {
        ReplaceClaim(identity, ClaimTypes.NameIdentifier, subject);
    }

    RemoveClaims(identity, ClaimTypes.Role);
    foreach (var role in ResolveOidcRoles(identity, authSettings))
    {
        identity.AddClaim(new Claim(ClaimTypes.Role, role));
    }
}

static IReadOnlyList<string> ResolveOidcRoles(ClaimsIdentity identity, LocalAuthOptions authSettings)
{
    var groups = identity.FindAll(authSettings.Oidc.RolesClaimType)
        .Select(claim => claim.Value)
        .Where(value => !string.IsNullOrWhiteSpace(value))
        .ToHashSet(StringComparer.OrdinalIgnoreCase);

    var roles = new List<string>();
    if (MatchesAny(groups, authSettings.Oidc.AdminGroups))
    {
        roles.Add(SecurityRoles.Admin);
    }

    if (MatchesAny(groups, authSettings.Oidc.OperatorGroups))
    {
        roles.Add(SecurityRoles.Operator);
    }

    if (MatchesAny(groups, authSettings.Oidc.ViewerGroups) || roles.Count == 0)
    {
        roles.Add(SecurityRoles.Viewer);
    }

    return roles.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
}

static bool MatchesAny(HashSet<string> groups, IEnumerable<string> candidates) =>
    candidates.Any(candidate => groups.Contains(candidate));

static Task HandleAuthRedirectAsync(Microsoft.AspNetCore.Authentication.RedirectContext<CookieAuthenticationOptions> context, int statusCode)
{
    if (context.Request.Path.StartsWithSegments("/api", StringComparison.OrdinalIgnoreCase))
    {
        context.Response.StatusCode = statusCode;
        return Task.CompletedTask;
    }

    context.Response.Redirect(context.RedirectUri);
    return Task.CompletedTask;
}

static string ResolveRequestedBy(ClaimsPrincipal user, string fallback) =>
    string.IsNullOrWhiteSpace(user.Identity?.Name)
        ? fallback
        : user.Identity!.Name!;

static void ReplaceClaim(ClaimsIdentity identity, string claimType, string value)
{
    var existingClaim = identity.FindFirst(claimType);
    if (existingClaim is not null && string.Equals(existingClaim.Value, value, StringComparison.Ordinal))
    {
        return;
    }

    if (existingClaim is not null)
    {
        identity.RemoveClaim(existingClaim);
    }

    identity.AddClaim(new Claim(claimType, value));
}

static void RemoveClaims(ClaimsIdentity identity, string claimType)
{
    foreach (var claim in identity.FindAll(claimType).ToArray())
    {
        identity.RemoveClaim(claim);
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

static void ConfigureApplicationInsights(WebApplicationBuilder builder)
{
    if (!IsApplicationInsightsConfigured(builder.Configuration))
    {
        return;
    }

    builder.Services.AddApplicationInsightsTelemetry();
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

public partial class Program
{
}

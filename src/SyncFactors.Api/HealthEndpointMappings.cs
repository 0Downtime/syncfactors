using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http.HttpResults;
using SyncFactors.Contracts;
using SyncFactors.Domain;

namespace SyncFactors.Api;

public static class HealthEndpointMappings
{
    private static readonly TimeSpan WorkerHeartbeatMaxAge = TimeSpan.FromSeconds(45);
    private static readonly TimeSpan FutureHeartbeatClockSkew = TimeSpan.FromSeconds(5);

    public static IEndpointRouteBuilder MapPublicHealthEndpoints(this IEndpointRouteBuilder endpoints, string detailedHealthPolicy)
    {
        var api = endpoints.MapGroup("/api");
        api.MapGet("/health", GetApiHealthAsync).RequireAuthorization(detailedHealthPolicy);
        endpoints.MapGet("/healthz", GetHealthz).AllowAnonymous();
        endpoints.MapGet("/readyz", GetReadyzAsync).AllowAnonymous();
        return endpoints;
    }

    public static async Task<Ok<DependencyHealthSnapshot>> GetApiHealthAsync(
        IDependencyHealthService healthService,
        CancellationToken cancellationToken)
    {
        var snapshot = await healthService.GetSnapshotAsync(cancellationToken);
        return TypedResults.Ok(snapshot);
    }

    public static async Task<Ok<DependencyHealthSnapshot>> GetDashboardHealthAsync(
        IDependencyHealthService healthService,
        DashboardSettingsProvider dashboardSettingsProvider,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        var settings = await dashboardSettingsProvider.GetHealthProbeStateAsync(cancellationToken);
        if (!settings.Enabled)
        {
            return TypedResults.Ok(CreateDisabledDashboardSnapshot(timeProvider.GetUtcNow()));
        }

        var snapshot = await healthService.GetSnapshotAsync(cancellationToken);
        return TypedResults.Ok(snapshot);
    }

    public static Ok<HealthzResponse> GetHealthz() => TypedResults.Ok(new HealthzResponse("ok"));

    public static async Task<IResult> GetReadyzAsync(
        IWorkerHeartbeatStore workerHeartbeatStore,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        var heartbeat = await workerHeartbeatStore.GetCurrentAsync(cancellationToken);
        var now = timeProvider.GetUtcNow();
        var workerReady = heartbeat is not null &&
            string.Equals(heartbeat.Service, "SyncFactors.Worker", StringComparison.Ordinal) &&
            !string.Equals(heartbeat.State, "Stopping", StringComparison.OrdinalIgnoreCase) &&
            heartbeat.LastSeenAt <= now.Add(FutureHeartbeatClockSkew) &&
            now - heartbeat.LastSeenAt <= WorkerHeartbeatMaxAge;

        return workerReady
            ? TypedResults.Ok(new HealthzResponse("ready"))
            : TypedResults.Json(new HealthzResponse("not-ready"), statusCode: StatusCodes.Status503ServiceUnavailable);
    }

    internal static DependencyHealthSnapshot CreateDisabledDashboardSnapshot(DateTimeOffset checkedAt) =>
        new(
            Status: "Disabled",
            CheckedAt: checkedAt,
            Probes: []);
}

public sealed record HealthzResponse(string Status);

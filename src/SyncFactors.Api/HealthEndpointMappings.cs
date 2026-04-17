using Microsoft.AspNetCore.Http.HttpResults;
using SyncFactors.Contracts;
using SyncFactors.Domain;

namespace SyncFactors.Api;

public static class HealthEndpointMappings
{
    public static IEndpointRouteBuilder MapPublicHealthEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var api = endpoints.MapGroup("/api");
        api.MapGet("/health", GetApiHealthAsync).AllowAnonymous();
        endpoints.MapGet("/healthz", GetHealthz).AllowAnonymous();
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

    internal static DependencyHealthSnapshot CreateDisabledDashboardSnapshot(DateTimeOffset checkedAt) =>
        new(
            Status: "Disabled",
            CheckedAt: checkedAt,
            Probes: []);
}

public sealed record HealthzResponse(string Status);

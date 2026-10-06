using System.Globalization;
using System.Net;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http.HttpResults;
using SyncFactors.Contracts;
using SyncFactors.Domain;
using SyncFactors.Infrastructure;

namespace SyncFactors.Api;

public static class HealthEndpointMappings
{
    public const string DeploymentNonceHeader = "X-SyncFactors-Deployment-Nonce";
    public const string ExpectedWorkerVersionHeader = "X-SyncFactors-Expected-Worker-Version";
    public const string ExpectedWorkerCommitHeader = "X-SyncFactors-Expected-Worker-Commit";
    public const string WorkerStartedAfterHeader = "X-SyncFactors-Worker-Started-After";
    public const string ExpectedApiVersionHeader = "X-SyncFactors-Expected-Api-Version";
    public const string ExpectedApiCommitHeader = "X-SyncFactors-Expected-Api-Commit";

    private static readonly TimeSpan WorkerHeartbeatMaxAge = TimeSpan.FromSeconds(45);
    private static readonly TimeSpan FutureHeartbeatClockSkew = TimeSpan.FromSeconds(5);
    private static readonly RuntimeBuildInfo ApiBuildInfo = RuntimeBuildInfo.FromAssembly(typeof(HealthEndpointMappings).Assembly);

    public static IEndpointRouteBuilder MapPublicHealthEndpoints(this IEndpointRouteBuilder endpoints, string detailedHealthPolicy)
    {
        var api = endpoints.MapGroup("/api");
        api.MapGet("/health", GetApiHealthAsync).RequireAuthorization(detailedHealthPolicy);
        endpoints.MapGet("/healthz", GetHealthz).AllowAnonymous();
        endpoints.MapGet("/readyz", GetReadyzWithAttestationAsync).AllowAnonymous();
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
        var workerReady = IsWorkerReady(heartbeat, now, requirements: null);

        return workerReady
            ? TypedResults.Ok(new HealthzResponse("ready"))
            : NotReady();
    }

    internal static async Task<IResult> GetReadyzWithAttestationAsync(
        IWorkerHeartbeatStore workerHeartbeatStore,
        IDependencyHealthService dependencyHealthService,
        TimeProvider timeProvider,
        HttpRequest request,
        CancellationToken cancellationToken)
    {
        if (!TryGetAttestationRequirements(request, out var requirements, out var attestationRequested))
        {
            return TypedResults.Json(
                new HealthzResponse("invalid-attestation"),
                statusCode: StatusCodes.Status400BadRequest);
        }

        if (!attestationRequested)
        {
            return await GetReadyzAsync(workerHeartbeatStore, timeProvider, cancellationToken);
        }

        if (!IsLoopback(request.HttpContext.Connection.RemoteIpAddress))
        {
            return TypedResults.Json(
                new HealthzResponse("not-ready"),
                statusCode: StatusCodes.Status403Forbidden);
        }

        if (!ApiMatches(requirements!))
        {
            return NotReady();
        }

        var heartbeat = await workerHeartbeatStore.GetCurrentAsync(cancellationToken);
        if (!IsWorkerReady(heartbeat, timeProvider.GetUtcNow(), requirements))
        {
            return NotReady();
        }

        try
        {
            var snapshot = await dependencyHealthService.GetSnapshotAsync(cancellationToken);
            if (!HasHealthyDeploymentDependencies(snapshot))
            {
                return NotReady();
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return NotReady();
        }

        return TypedResults.Ok(new AttestedHealthzResponse("ready", Attested: true));
    }

    private static bool IsWorkerReady(
        WorkerHeartbeat? heartbeat,
        DateTimeOffset now,
        WorkerReadinessRequirements? requirements)
    {
        var workerReady = heartbeat is not null &&
            string.Equals(heartbeat.Service, "SyncFactors.Worker", StringComparison.Ordinal) &&
            !string.Equals(heartbeat.State, "Stopping", StringComparison.OrdinalIgnoreCase) &&
            heartbeat.LastSeenAt <= now.Add(FutureHeartbeatClockSkew) &&
            now - heartbeat.LastSeenAt <= WorkerHeartbeatMaxAge;

        if (!workerReady || requirements is null || heartbeat is null)
        {
            return workerReady;
        }

        return !string.IsNullOrWhiteSpace(heartbeat.InstanceId) &&
               WorkerDeploymentAttestation.MatchesNonce(
                   requirements.DeploymentNonce,
                   heartbeat.DeploymentNonceHash) &&
               (requirements.WorkerStartedAfter is null ||
                heartbeat.StartedAt > requirements.WorkerStartedAfter.Value) &&
               (requirements.ExpectedWorkerVersion is null ||
                string.Equals(
                    heartbeat.BuildVersion,
                    requirements.ExpectedWorkerVersion,
                    StringComparison.Ordinal)) &&
               (requirements.ExpectedWorkerCommit is null ||
                string.Equals(
                    heartbeat.BuildCommitSha,
                    requirements.ExpectedWorkerCommit,
                    StringComparison.OrdinalIgnoreCase));
    }

    private static bool TryGetAttestationRequirements(
        HttpRequest request,
        out WorkerReadinessRequirements? requirements,
        out bool attestationRequested)
    {
        requirements = null;
        var attestationHeaders = new[]
        {
            DeploymentNonceHeader,
            ExpectedWorkerVersionHeader,
            ExpectedWorkerCommitHeader,
            WorkerStartedAfterHeader,
            ExpectedApiVersionHeader,
            ExpectedApiCommitHeader
        };
        attestationRequested = attestationHeaders.Any(request.Headers.ContainsKey);
        if (!attestationRequested)
        {
            return true;
        }

        if (!TryReadHeader(request, DeploymentNonceHeader, required: true, out var deploymentNonce) ||
            deploymentNonce!.Length < WorkerDeploymentAttestation.MinimumNonceLength ||
            !TryReadHeader(request, ExpectedWorkerVersionHeader, required: false, out var expectedVersion) ||
            !TryReadHeader(request, ExpectedWorkerCommitHeader, required: false, out var expectedCommit) ||
            !TryReadHeader(request, WorkerStartedAfterHeader, required: false, out var startedAfterValue) ||
            !TryReadHeader(request, ExpectedApiVersionHeader, required: false, out var expectedApiVersion) ||
            !TryReadHeader(request, ExpectedApiCommitHeader, required: false, out var expectedApiCommit))
        {
            return false;
        }

        DateTimeOffset? startedAfter = null;
        if (startedAfterValue is not null)
        {
            if (!DateTimeOffset.TryParse(
                    startedAfterValue,
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.RoundtripKind,
                    out var parsedStartedAfter))
            {
                return false;
            }

            startedAfter = parsedStartedAfter;
        }

        requirements = new WorkerReadinessRequirements(
            deploymentNonce,
            expectedVersion,
            expectedCommit,
            startedAfter,
            expectedApiVersion,
            expectedApiCommit);
        return true;
    }

    private static bool ApiMatches(WorkerReadinessRequirements requirements) =>
        (requirements.ExpectedApiVersion is null ||
         string.Equals(ApiBuildInfo.Version, requirements.ExpectedApiVersion, StringComparison.Ordinal)) &&
        (requirements.ExpectedApiCommit is null ||
         string.Equals(ApiBuildInfo.CommitSha, requirements.ExpectedApiCommit, StringComparison.OrdinalIgnoreCase));

    private static bool TryReadHeader(
        HttpRequest request,
        string name,
        bool required,
        out string? value)
    {
        value = null;
        if (!request.Headers.TryGetValue(name, out var values))
        {
            return !required;
        }

        if (values.Count != 1 || string.IsNullOrWhiteSpace(values[0]))
        {
            return false;
        }

        value = values[0]!.Trim();
        return true;
    }

    private static bool HasHealthyDeploymentDependencies(DependencyHealthSnapshot snapshot)
    {
        if (!string.Equals(snapshot.Status, DependencyHealthStates.Healthy, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var requiredDependencies = new[] { "SuccessFactors", "Active Directory", "SQLite" };
        return requiredDependencies.All(required =>
            snapshot.Probes.Any(probe =>
                string.Equals(probe.Dependency, required, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(probe.Status, DependencyHealthStates.Healthy, StringComparison.OrdinalIgnoreCase)));
    }

    private static bool IsLoopback(IPAddress? address) =>
        address is not null &&
        (IPAddress.IsLoopback(address) ||
         (address.IsIPv4MappedToIPv6 && IPAddress.IsLoopback(address.MapToIPv4())));

    private static Microsoft.AspNetCore.Http.HttpResults.JsonHttpResult<HealthzResponse> NotReady() =>
        TypedResults.Json(
            new HealthzResponse("not-ready"),
            statusCode: StatusCodes.Status503ServiceUnavailable);

    internal static DependencyHealthSnapshot CreateDisabledDashboardSnapshot(DateTimeOffset checkedAt) =>
        new(
            Status: "Disabled",
            CheckedAt: checkedAt,
            Probes: []);
}

internal sealed record WorkerReadinessRequirements(
    string DeploymentNonce,
    string? ExpectedWorkerVersion,
    string? ExpectedWorkerCommit,
    DateTimeOffset? WorkerStartedAfter,
    string? ExpectedApiVersion,
    string? ExpectedApiCommit);

public sealed record HealthzResponse(string Status);

public sealed record AttestedHealthzResponse(string Status, bool Attested);

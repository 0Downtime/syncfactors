using Microsoft.AspNetCore.Http;
using System.Text.Json;
using SyncFactors.Contracts;
using SyncFactors.Domain;
using SyncFactors.Infrastructure;

namespace SyncFactors.Api.Tests;

public sealed class HealthEndpointsTests
{
    [Fact]
    public async Task ApiHealth_ReturnsSnapshot()
    {
        var result = await HealthEndpointMappings.GetApiHealthAsync(new StubDependencyHealthService(), CancellationToken.None);

        Assert.NotNull(result.Value);
        Assert.NotEmpty(result.Value.Probes);
    }

    [Fact]
    public async Task DashboardHealth_ReturnsDisabledSnapshot_WhenDashboardHealthProbesAreDisabled()
    {
        var result = await HealthEndpointMappings.GetDashboardHealthAsync(
            new StubDependencyHealthService(),
            new DashboardSettingsProvider(
                new DashboardOptions(DefaultHealthProbesEnabled: false, DefaultHealthProbeIntervalSeconds: 45),
                new StubDashboardSettingsStore(enabledOverride: null)),
            new StubTimeProvider(DateTimeOffset.Parse("2026-04-17T12:00:00Z")),
            CancellationToken.None);

        Assert.NotNull(result.Value);
        Assert.Equal("Disabled", result.Value.Status);
        Assert.Empty(result.Value.Probes);
    }

    [Fact]
    public void Healthz_ReturnsOkPayload()
    {
        var result = HealthEndpointMappings.GetHealthz();

        Assert.NotNull(result.Value);
        Assert.Equal("ok", result.Value.Status);
    }

    [Fact]
    public async Task Readyz_ReturnsReady_WhenWorkerHeartbeatIsCurrent()
    {
        var now = DateTimeOffset.Parse("2026-04-17T12:00:00Z");
        var result = await HealthEndpointMappings.GetReadyzAsync(
            new StubWorkerHeartbeatStore(new WorkerHeartbeat(
                "SyncFactors.Worker",
                "Idle",
                "Waiting for work.",
                now.AddMinutes(-5),
                now.AddSeconds(-10))),
            new StubTimeProvider(now),
            CancellationToken.None);

        var ok = Assert.IsType<Microsoft.AspNetCore.Http.HttpResults.Ok<HealthzResponse>>(result);
        Assert.NotNull(ok.Value);
        Assert.Equal("ready", ok.Value.Status);
        var payload = JsonSerializer.Serialize(ok.Value, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        Assert.DoesNotContain("\"attested\"", payload, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(-46, "Idle")]
    [InlineData(-10, "Stopping")]
    [InlineData(6, "Idle")]
    public async Task Readyz_ReturnsServiceUnavailable_WhenWorkerIsNotReady(int lastSeenOffsetSeconds, string state)
    {
        var now = DateTimeOffset.Parse("2026-04-17T12:00:00Z");
        var result = await HealthEndpointMappings.GetReadyzAsync(
            new StubWorkerHeartbeatStore(new WorkerHeartbeat(
                "SyncFactors.Worker",
                state,
                null,
                now.AddMinutes(-5),
                now.AddSeconds(lastSeenOffsetSeconds))),
            new StubTimeProvider(now),
            CancellationToken.None);

        var unavailable = Assert.IsType<Microsoft.AspNetCore.Http.HttpResults.JsonHttpResult<HealthzResponse>>(result);
        Assert.Equal(Microsoft.AspNetCore.Http.StatusCodes.Status503ServiceUnavailable, unavailable.StatusCode);
        Assert.Equal("not-ready", unavailable.Value?.Status);
    }

    [Fact]
    public async Task Readyz_ReturnsServiceUnavailable_WhenWorkerHasNotStarted()
    {
        var result = await HealthEndpointMappings.GetReadyzAsync(
            new StubWorkerHeartbeatStore(null),
            new StubTimeProvider(DateTimeOffset.Parse("2026-04-17T12:00:00Z")),
            CancellationToken.None);

        var unavailable = Assert.IsType<Microsoft.AspNetCore.Http.HttpResults.JsonHttpResult<HealthzResponse>>(result);
        Assert.Equal(Microsoft.AspNetCore.Http.StatusCodes.Status503ServiceUnavailable, unavailable.StatusCode);
    }

    [Fact]
    public async Task Readyz_AttestationRejectsFreshHeartbeatFromProcessStartedBeforeDeployment()
    {
        var now = DateTimeOffset.Parse("2026-04-17T12:00:00Z");
        var deploymentStartedAt = now.AddMinutes(-2);
        var nonce = new string('n', WorkerDeploymentAttestation.MinimumNonceLength);
        var dependencyHealth = new StubDependencyHealthService();
        var request = CreateAttestationRequest(
            nonce,
            workerStartedAfter: deploymentStartedAt,
            expectedVersion: "1.2.3",
            expectedCommit: "0123456789abcdef");
        var heartbeat = CreateAttestableHeartbeat(
            now,
            nonce,
            startedAt: deploymentStartedAt.AddSeconds(-1));

        var result = await HealthEndpointMappings.GetReadyzWithAttestationAsync(
            new StubWorkerHeartbeatStore(heartbeat),
            dependencyHealth,
            new StubTimeProvider(now),
            request,
            CancellationToken.None);

        AssertNotReady(result);
        Assert.Equal(0, dependencyHealth.Calls);
    }

    [Theory]
    [InlineData("1.2.4", "0123456789abcdef")]
    [InlineData("1.2.3", "fedcba9876543210")]
    public async Task Readyz_AttestationRejectsWorkerBuildMismatch(
        string expectedVersion,
        string expectedCommit)
    {
        var now = DateTimeOffset.Parse("2026-04-17T12:00:00Z");
        var nonce = new string('n', WorkerDeploymentAttestation.MinimumNonceLength);
        var dependencyHealth = new StubDependencyHealthService();
        var request = CreateAttestationRequest(
            nonce,
            workerStartedAfter: now.AddMinutes(-2),
            expectedVersion: expectedVersion,
            expectedCommit: expectedCommit);

        var result = await HealthEndpointMappings.GetReadyzWithAttestationAsync(
            new StubWorkerHeartbeatStore(CreateAttestableHeartbeat(now, nonce)),
            dependencyHealth,
            new StubTimeProvider(now),
            request,
            CancellationToken.None);

        AssertNotReady(result);
        Assert.Equal(0, dependencyHealth.Calls);
    }

    [Fact]
    public async Task Readyz_AttestationRequiresRotatedDeploymentNonce()
    {
        var now = DateTimeOffset.Parse("2026-04-17T12:00:00Z");
        var oldNonce = new string('o', WorkerDeploymentAttestation.MinimumNonceLength);
        var newNonce = new string('n', WorkerDeploymentAttestation.MinimumNonceLength);
        var dependencyHealth = new StubDependencyHealthService();

        var result = await HealthEndpointMappings.GetReadyzWithAttestationAsync(
            new StubWorkerHeartbeatStore(CreateAttestableHeartbeat(now, oldNonce)),
            dependencyHealth,
            new StubTimeProvider(now),
            CreateAttestationRequest(newNonce, workerStartedAfter: now.AddMinutes(-2)),
            CancellationToken.None);

        AssertNotReady(result);
        Assert.Equal(0, dependencyHealth.Calls);
    }

    [Fact]
    public async Task Readyz_AttestationPerformsFunctionalDependencyChecksAndReturnsSummaryOnly()
    {
        var now = DateTimeOffset.Parse("2026-04-17T12:00:00Z");
        var nonce = new string('n', WorkerDeploymentAttestation.MinimumNonceLength);
        var dependencyHealth = new StubDependencyHealthService(
            dependencyStatus: DependencyHealthStates.Unhealthy);

        var result = await HealthEndpointMappings.GetReadyzWithAttestationAsync(
            new StubWorkerHeartbeatStore(CreateAttestableHeartbeat(now, nonce)),
            dependencyHealth,
            new StubTimeProvider(now),
            CreateAttestationRequest(nonce, workerStartedAfter: now.AddMinutes(-2)),
            CancellationToken.None);

        var unavailable = AssertNotReady(result);
        Assert.Equal("not-ready", unavailable.Value?.Status);
        Assert.Equal(1, dependencyHealth.Calls);
    }

    [Fact]
    public async Task Readyz_AttestationReturnsReadyForNewExpectedWorkerAndHealthyDependencies()
    {
        var now = DateTimeOffset.Parse("2026-04-17T12:00:00Z");
        var nonce = new string('n', WorkerDeploymentAttestation.MinimumNonceLength);
        var dependencyHealth = new StubDependencyHealthService();
        var apiBuildInfo = RuntimeBuildInfo.FromAssembly(typeof(HealthEndpointMappings).Assembly);

        var result = await HealthEndpointMappings.GetReadyzWithAttestationAsync(
            new StubWorkerHeartbeatStore(CreateAttestableHeartbeat(now, nonce)),
            dependencyHealth,
            new StubTimeProvider(now),
            CreateAttestationRequest(
                nonce,
                workerStartedAfter: now.AddMinutes(-2),
                expectedVersion: "1.2.3",
                expectedCommit: "0123456789abcdef",
                expectedApiVersion: apiBuildInfo.Version,
                expectedApiCommit: apiBuildInfo.CommitSha),
            CancellationToken.None);

        var ok = Assert.IsType<Microsoft.AspNetCore.Http.HttpResults.Ok<AttestedHealthzResponse>>(result);
        Assert.NotNull(ok.Value);
        Assert.Equal("ready", ok.Value.Status);
        Assert.True(ok.Value.Attested);
        var payload = JsonSerializer.Serialize(ok.Value, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        Assert.Contains("\"attested\":true", payload, StringComparison.Ordinal);
        Assert.Equal(1, dependencyHealth.Calls);
    }

    [Theory]
    [InlineData("wrong-api-version", null)]
    [InlineData(null, "fedcba9876543210")]
    public async Task Readyz_AttestationRejectsApiBuildMismatch(
        string? expectedApiVersion,
        string? expectedApiCommit)
    {
        var now = DateTimeOffset.Parse("2026-04-17T12:00:00Z");
        var nonce = new string('n', WorkerDeploymentAttestation.MinimumNonceLength);
        var dependencyHealth = new StubDependencyHealthService();

        var result = await HealthEndpointMappings.GetReadyzWithAttestationAsync(
            new StubWorkerHeartbeatStore(CreateAttestableHeartbeat(now, nonce)),
            dependencyHealth,
            new StubTimeProvider(now),
            CreateAttestationRequest(
                nonce,
                workerStartedAfter: now.AddMinutes(-2),
                expectedApiVersion: expectedApiVersion,
                expectedApiCommit: expectedApiCommit),
            CancellationToken.None);

        AssertNotReady(result);
        Assert.Equal(0, dependencyHealth.Calls);
    }

    [Fact]
    public async Task Readyz_AttestationHeadersWithoutNonceAreRejectedWithoutDependencyProbe()
    {
        var now = DateTimeOffset.Parse("2026-04-17T12:00:00Z");
        var dependencyHealth = new StubDependencyHealthService();
        var context = new Microsoft.AspNetCore.Http.DefaultHttpContext();
        context.Connection.RemoteIpAddress = System.Net.IPAddress.Loopback;
        context.Request.Headers[HealthEndpointMappings.ExpectedWorkerCommitHeader] = "0123456789abcdef";

        var result = await HealthEndpointMappings.GetReadyzWithAttestationAsync(
            new StubWorkerHeartbeatStore(CreateAttestableHeartbeat(
                now,
                new string('n', WorkerDeploymentAttestation.MinimumNonceLength))),
            dependencyHealth,
            new StubTimeProvider(now),
            context.Request,
            CancellationToken.None);

        var invalid = Assert.IsType<Microsoft.AspNetCore.Http.HttpResults.JsonHttpResult<HealthzResponse>>(result);
        Assert.Equal(Microsoft.AspNetCore.Http.StatusCodes.Status400BadRequest, invalid.StatusCode);
        Assert.Equal("invalid-attestation", invalid.Value?.Status);
        Assert.Equal(0, dependencyHealth.Calls);
    }

    [Fact]
    public async Task Readyz_WithoutAttestationHeadersRemainsCheap()
    {
        var now = DateTimeOffset.Parse("2026-04-17T12:00:00Z");
        var dependencyHealth = new StubDependencyHealthService(
            dependencyStatus: DependencyHealthStates.Unhealthy);
        var context = new Microsoft.AspNetCore.Http.DefaultHttpContext();

        var result = await HealthEndpointMappings.GetReadyzWithAttestationAsync(
            new StubWorkerHeartbeatStore(CreateAttestableHeartbeat(
                now,
                new string('n', WorkerDeploymentAttestation.MinimumNonceLength))),
            dependencyHealth,
            new StubTimeProvider(now),
            context.Request,
            CancellationToken.None);

        var ok = Assert.IsType<Microsoft.AspNetCore.Http.HttpResults.Ok<HealthzResponse>>(result);
        Assert.Equal("ready", ok.Value?.Status);
        Assert.Equal(0, dependencyHealth.Calls);
    }

    [Fact]
    public async Task Readyz_AttestationIsRejectedForNonLoopbackCaller()
    {
        var now = DateTimeOffset.Parse("2026-04-17T12:00:00Z");
        var nonce = new string('n', WorkerDeploymentAttestation.MinimumNonceLength);
        var dependencyHealth = new StubDependencyHealthService();
        var request = CreateAttestationRequest(nonce, workerStartedAfter: now.AddMinutes(-2));
        request.HttpContext.Connection.RemoteIpAddress = System.Net.IPAddress.Parse("192.0.2.10");

        var result = await HealthEndpointMappings.GetReadyzWithAttestationAsync(
            new StubWorkerHeartbeatStore(CreateAttestableHeartbeat(now, nonce)),
            dependencyHealth,
            new StubTimeProvider(now),
            request,
            CancellationToken.None);

        var forbidden = Assert.IsType<Microsoft.AspNetCore.Http.HttpResults.JsonHttpResult<HealthzResponse>>(result);
        Assert.Equal(Microsoft.AspNetCore.Http.StatusCodes.Status403Forbidden, forbidden.StatusCode);
        Assert.Equal("not-ready", forbidden.Value?.Status);
        Assert.Equal(0, dependencyHealth.Calls);
    }

    private static WorkerHeartbeat CreateAttestableHeartbeat(
        DateTimeOffset now,
        string nonce,
        DateTimeOffset? startedAt = null) =>
        new(
            Service: "SyncFactors.Worker",
            State: "Idle",
            Activity: "Waiting for work.",
            StartedAt: startedAt ?? now.AddMinutes(-1),
            LastSeenAt: now.AddSeconds(-10),
            InstanceId: "worker-instance-123",
            BuildVersion: "1.2.3",
            BuildCommitSha: "0123456789abcdef",
            DeploymentNonceHash: WorkerDeploymentAttestation.HashConfiguredNonce(nonce));

    private static Microsoft.AspNetCore.Http.HttpRequest CreateAttestationRequest(
        string nonce,
        DateTimeOffset? workerStartedAfter = null,
        string? expectedVersion = null,
        string? expectedCommit = null,
        string? expectedApiVersion = null,
        string? expectedApiCommit = null)
    {
        var context = new Microsoft.AspNetCore.Http.DefaultHttpContext();
        context.Connection.RemoteIpAddress = System.Net.IPAddress.Loopback;
        context.Request.Headers[HealthEndpointMappings.DeploymentNonceHeader] = nonce;
        if (workerStartedAfter is not null)
        {
            context.Request.Headers[HealthEndpointMappings.WorkerStartedAfterHeader] = workerStartedAfter.Value.ToString("O");
        }

        if (expectedVersion is not null)
        {
            context.Request.Headers[HealthEndpointMappings.ExpectedWorkerVersionHeader] = expectedVersion;
        }

        if (expectedCommit is not null)
        {
            context.Request.Headers[HealthEndpointMappings.ExpectedWorkerCommitHeader] = expectedCommit;
        }

        if (expectedApiVersion is not null)
        {
            context.Request.Headers[HealthEndpointMappings.ExpectedApiVersionHeader] = expectedApiVersion;
        }

        if (expectedApiCommit is not null)
        {
            context.Request.Headers[HealthEndpointMappings.ExpectedApiCommitHeader] = expectedApiCommit;
        }

        return context.Request;
    }

    private static Microsoft.AspNetCore.Http.HttpResults.JsonHttpResult<HealthzResponse> AssertNotReady(IResult result)
    {
        var unavailable = Assert.IsType<Microsoft.AspNetCore.Http.HttpResults.JsonHttpResult<HealthzResponse>>(result);
        Assert.Equal(Microsoft.AspNetCore.Http.StatusCodes.Status503ServiceUnavailable, unavailable.StatusCode);
        Assert.Equal("not-ready", unavailable.Value?.Status);
        return unavailable;
    }

    private sealed class StubDependencyHealthService(
        string dependencyStatus = DependencyHealthStates.Healthy) : IDependencyHealthService
    {
        public int Calls { get; private set; }

        public Task<DependencyHealthSnapshot> GetSnapshotAsync(CancellationToken cancellationToken)
        {
            Calls++;
            var checkedAt = DateTimeOffset.Parse("2026-04-12T12:00:00Z");
            return Task.FromResult(new DependencyHealthSnapshot(
                Status: dependencyStatus,
                CheckedAt: checkedAt,
                Probes:
                [
                    new DependencyProbeResult(
                        Dependency: "SQLite",
                        Status: dependencyStatus,
                        Summary: "Operational store opened successfully.",
                        Details: "/tmp/runtime.db",
                        CheckedAt: checkedAt,
                        DurationMilliseconds: 3,
                        ObservedAt: checkedAt,
                        IsStale: false),
                    new DependencyProbeResult(
                        Dependency: "SuccessFactors",
                        Status: dependencyStatus,
                        Summary: "Authenticated read probe completed.",
                        Details: null,
                        CheckedAt: checkedAt,
                        DurationMilliseconds: 3,
                        ObservedAt: checkedAt,
                        IsStale: false),
                    new DependencyProbeResult(
                        Dependency: "Active Directory",
                        Status: dependencyStatus,
                        Summary: "LDAP bind and lookup probe completed.",
                        Details: null,
                        CheckedAt: checkedAt,
                        DurationMilliseconds: 3,
                        ObservedAt: checkedAt,
                        IsStale: false)
                ]));
        }
    }

    private sealed class StubDashboardSettingsStore(bool? enabledOverride) : IDashboardSettingsStore
    {
        public Task<bool?> GetHealthProbesEnabledOverrideAsync(CancellationToken cancellationToken)
        {
            _ = cancellationToken;
            return Task.FromResult(enabledOverride);
        }

        public Task<int?> GetHealthProbeIntervalSecondsOverrideAsync(CancellationToken cancellationToken)
        {
            _ = cancellationToken;
            return Task.FromResult<int?>(null);
        }

        public Task SaveHealthProbeOverrideAsync(bool enabled, int intervalSeconds, CancellationToken cancellationToken)
        {
            _ = cancellationToken;
            return Task.CompletedTask;
        }
    }

    private sealed class StubWorkerHeartbeatStore(WorkerHeartbeat? heartbeat) : IWorkerHeartbeatStore
    {
        public Task<WorkerHeartbeat?> GetCurrentAsync(CancellationToken cancellationToken) =>
            Task.FromResult(heartbeat);

        public Task SaveAsync(WorkerHeartbeat heartbeat, CancellationToken cancellationToken) =>
            Task.CompletedTask;
    }

    private sealed class StubTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => utcNow;
    }
}

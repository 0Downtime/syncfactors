using SyncFactors.Contracts;
using SyncFactors.Domain;

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

    private sealed class StubDependencyHealthService : IDependencyHealthService
    {
        public Task<DependencyHealthSnapshot> GetSnapshotAsync(CancellationToken cancellationToken)
        {
            var checkedAt = DateTimeOffset.Parse("2026-04-12T12:00:00Z");
            return Task.FromResult(new DependencyHealthSnapshot(
                Status: DependencyHealthStates.Healthy,
                CheckedAt: checkedAt,
                Probes:
                [
                    new DependencyProbeResult(
                        Dependency: "SQLite",
                        Status: DependencyHealthStates.Healthy,
                        Summary: "Operational store opened successfully.",
                        Details: "/tmp/runtime.db",
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

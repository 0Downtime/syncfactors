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
    public void Healthz_ReturnsOkPayload()
    {
        var result = HealthEndpointMappings.GetHealthz();

        Assert.NotNull(result.Value);
        Assert.Equal("ok", result.Value.Status);
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
}

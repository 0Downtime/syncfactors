using Microsoft.Extensions.Logging.Abstractions;
using SyncFactors.Contracts;
using SyncFactors.Domain;
using SyncFactors.Infrastructure;
using System.Net;
using System.Net.Http;
using System.Threading;

namespace SyncFactors.Infrastructure.Tests;

public sealed class DependencyHealthServiceTests
{
    [Fact]
    public async Task GetSnapshotAsync_ReturnsTimedOutProbe_WhenSuccessFactorsReadHangs()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "syncfactors-health-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);

        var databasePath = Path.Combine(tempRoot, "runtime.db");
        var pathResolver = new SqlitePathResolver(databasePath);
        var initializer = new SqliteDatabaseInitializer(pathResolver);
        await initializer.InitializeAsync(CancellationToken.None);

        IWorkerHeartbeatStore heartbeatStore = new StubWorkerHeartbeatStore(
            new WorkerHeartbeat(
                Service: "SyncFactors.Worker",
                State: "Idle",
                Activity: "Waiting for scheduled work.",
                StartedAt: DateTimeOffset.Parse("2026-03-27T12:00:00Z"),
                LastSeenAt: DateTimeOffset.Parse("2026-03-27T12:00:15Z")));

        var configLoader = new SyncFactorsConfigurationLoader(
            new SyncFactorsConfigPathResolver(
                Path.Combine(tempRoot, "sync-config.json"),
                null));

        await File.WriteAllTextAsync(
            Path.Combine(tempRoot, "sync-config.json"),
            """
            {
              "secrets": {},
              "ad": {
                "server": "ldap.example.invalid:389",
                "username": "",
                "bindPassword": "",
                "defaultActiveOu": "OU=LabUsers,DC=example,DC=com",
                "graveyardOu": "OU=Graveyard,DC=example,DC=com",
                "identityAttribute": "employeeID"
              },
              "successFactors": {
                "baseUrl": "https://example.invalid/odata/v2",
                "query": {
                  "entitySet": "PerPerson",
                  "identityField": "personIdExternal",
                  "deltaField": "lastModifiedDateTime",
                  "select": [ "userId" ],
                  "expand": []
                },
                "auth": {
                  "mode": "basic",
                  "basic": {
                    "username": "user",
                    "password": "pass"
                  }
                }
              },
              "sync": {
                "enableBeforeStartDays": 7,
                "deletionRetentionDays": 30
              },
              "safety": {
                "maxCreatesPerRun": 25,
                "maxDisablesPerRun": 25,
                "maxDeletionsPerRun": 25
              },
              "reporting": {
                "outputDirectory": "/tmp"
              }
            }
            """);

        var service = new DependencyHealthService(
            configLoader,
            pathResolver,
            heartbeatStore,
            new HttpClient(new HangingMessageHandler()),
            new FakeTimeProvider(DateTimeOffset.Parse("2026-03-27T12:00:30Z")),
            NullLogger<DependencyHealthService>.Instance);

        var snapshot = await service.GetSnapshotAsync(CancellationToken.None);

        var successFactorsProbe = Assert.Single(snapshot.Probes, probe => probe.Dependency == "SuccessFactors");
        Assert.Equal(DependencyHealthStates.Unhealthy, successFactorsProbe.Status);
        Assert.Contains("timed out", successFactorsProbe.Summary, StringComparison.OrdinalIgnoreCase);
    }

    private sealed class StubWorkerHeartbeatStore(WorkerHeartbeat heartbeat) : IWorkerHeartbeatStore
    {
        public Task<WorkerHeartbeat?> GetCurrentAsync(CancellationToken cancellationToken)
        {
            _ = cancellationToken;
            return Task.FromResult<WorkerHeartbeat?>(heartbeat);
        }

        public Task SaveAsync(WorkerHeartbeat heartbeat, CancellationToken cancellationToken)
        {
            _ = heartbeat;
            _ = cancellationToken;
            return Task.CompletedTask;
        }
    }

    private sealed class HangingMessageHandler : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            _ = request;
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK);
        }
    }

    private sealed class FakeTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => utcNow;
    }
}

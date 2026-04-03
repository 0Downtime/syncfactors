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
    public async Task GetSnapshotAsync_ShowsFallbackTransportInActiveDirectoryProbe_OutsideProduction()
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
                "server": "ldap.example.invalid",
                "username": "",
                "bindPassword": "",
                "defaultActiveOu": "OU=LabUsers,DC=example,DC=com",
                "prehireOu": "OU=Prehire,DC=example,DC=com",
                "graveyardOu": "OU=Graveyard,DC=example,DC=com",
                "identityAttribute": "employeeID",
                "transport": {
                  "mode": "ldaps",
                  "allowLdapFallback": true
                }
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

        var originalDotnetEnvironment = Environment.GetEnvironmentVariable("DOTNET_ENVIRONMENT");

        try
        {
            Environment.SetEnvironmentVariable("DOTNET_ENVIRONMENT", "Development");

            var service = new DependencyHealthService(
                configLoader,
                pathResolver,
                heartbeatStore,
                new HttpClient(new SuccessMessageHandler()),
                new FakeTimeProvider(DateTimeOffset.Parse("2026-03-27T12:00:30Z")),
                NullLogger<DependencyHealthService>.Instance,
                activeDirectoryProbe: (_, _) => Task.FromResult(("ldaps", "ldap", true)));

            var snapshot = await service.GetSnapshotAsync(CancellationToken.None);

            var activeDirectoryProbe = Assert.Single(snapshot.Probes, probe => probe.Dependency == "Active Directory");
            Assert.Equal(DependencyHealthStates.Healthy, activeDirectoryProbe.Status);
            Assert.Contains("fallback LDAP", activeDirectoryProbe.Summary, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("Requested transport 'ldaps' failed", activeDirectoryProbe.Details, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Environment.SetEnvironmentVariable("DOTNET_ENVIRONMENT", originalDotnetEnvironment);
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public async Task GetSnapshotAsync_HidesFallbackTransportInActiveDirectoryProbe_InProduction()
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
                "server": "ldap.example.invalid",
                "username": "",
                "bindPassword": "",
                "defaultActiveOu": "OU=LabUsers,DC=example,DC=com",
                "prehireOu": "OU=Prehire,DC=example,DC=com",
                "graveyardOu": "OU=Graveyard,DC=example,DC=com",
                "identityAttribute": "employeeID",
                "transport": {
                  "mode": "ldaps",
                  "allowLdapFallback": true
                }
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

        var originalDotnetEnvironment = Environment.GetEnvironmentVariable("DOTNET_ENVIRONMENT");

        try
        {
            Environment.SetEnvironmentVariable("DOTNET_ENVIRONMENT", "Production");

            var service = new DependencyHealthService(
                configLoader,
                pathResolver,
                heartbeatStore,
                new HttpClient(new SuccessMessageHandler()),
                new FakeTimeProvider(DateTimeOffset.Parse("2026-03-27T12:00:30Z")),
                NullLogger<DependencyHealthService>.Instance,
                activeDirectoryProbe: (_, _) => Task.FromResult(("ldaps", "ldap", true)));

            var snapshot = await service.GetSnapshotAsync(CancellationToken.None);

            var activeDirectoryProbe = Assert.Single(snapshot.Probes, probe => probe.Dependency == "Active Directory");
            Assert.Equal(DependencyHealthStates.Healthy, activeDirectoryProbe.Status);
            Assert.Equal("LDAP bind and base search succeeded.", activeDirectoryProbe.Summary);
            Assert.Null(activeDirectoryProbe.Details);
        }
        finally
        {
            Environment.SetEnvironmentVariable("DOTNET_ENVIRONMENT", originalDotnetEnvironment);
            Directory.Delete(tempRoot, recursive: true);
        }
    }

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
                "prehireOu": "OU=Prehire,DC=example,DC=com",
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

    [Fact]
    public async Task GetSnapshotAsync_ReturnsTimedOutProbe_WhenActiveDirectoryProbeHangs()
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
                "server": "ldap.example.invalid",
                "username": "",
                "bindPassword": "",
                "defaultActiveOu": "OU=LabUsers,DC=example,DC=com",
                "prehireOu": "OU=Prehire,DC=example,DC=com",
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
            new HttpClient(new SuccessMessageHandler()),
            new FakeTimeProvider(DateTimeOffset.Parse("2026-03-27T12:00:30Z")),
            NullLogger<DependencyHealthService>.Instance,
            activeDirectoryProbe: static async (_, _) =>
            {
                await Task.Delay(Timeout.InfiniteTimeSpan);
                return ("ldaps", "ldaps", false);
            });

        var snapshot = await service.GetSnapshotAsync(CancellationToken.None);

        var activeDirectoryProbe = Assert.Single(snapshot.Probes, probe => probe.Dependency == "Active Directory");
        Assert.Equal(DependencyHealthStates.Unhealthy, activeDirectoryProbe.Status);
        Assert.Contains("timed out", activeDirectoryProbe.Summary, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task GetSnapshotAsync_RetriesWithoutRejectedSuccessFactorsSelectField()
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
                "prehireOu": "OU=Prehire,DC=example,DC=com",
                "graveyardOu": "OU=Graveyard,DC=example,DC=com",
                "identityAttribute": "employeeID"
              },
              "successFactors": {
                "baseUrl": "https://example.invalid/odata/v2",
                "query": {
                  "entitySet": "EmpJob",
                  "identityField": "userId",
                  "deltaField": "lastModifiedDateTime",
                  "select": [ "userId", "personIdExternal" ],
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

        var handler = new RetryOnInvalidPropertyMessageHandler();
        var service = new DependencyHealthService(
            configLoader,
            pathResolver,
            heartbeatStore,
            new HttpClient(handler),
            new FakeTimeProvider(DateTimeOffset.Parse("2026-03-27T12:00:30Z")),
            NullLogger<DependencyHealthService>.Instance);

        var snapshot = await service.GetSnapshotAsync(CancellationToken.None);

        var successFactorsProbe = Assert.Single(snapshot.Probes, probe => probe.Dependency == "SuccessFactors");
        Assert.Equal(DependencyHealthStates.Healthy, successFactorsProbe.Status);
        Assert.Contains("Authenticated read succeeded", successFactorsProbe.Summary, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("personIdExternal", successFactorsProbe.Details, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(2, handler.RequestUris.Count);
        Assert.Contains("$select=userId%2CpersonIdExternal", handler.RequestUris[0]);
        Assert.Contains("$select=userId", handler.RequestUris[1]);
    }

    [Fact]
    public async Task GetSnapshotAsync_ReturnsHttpFailure_WhenSuccessFactorsErrorPayloadIsJsonString()
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
                "prehireOu": "OU=Prehire,DC=example,DC=com",
                "graveyardOu": "OU=Graveyard,DC=example,DC=com",
                "identityAttribute": "employeeID"
              },
              "successFactors": {
                "baseUrl": "https://example.invalid/odata/v2",
                "query": {
                  "entitySet": "EmpJob",
                  "identityField": "userId",
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
            new HttpClient(new JsonStringErrorMessageHandler()),
            new FakeTimeProvider(DateTimeOffset.Parse("2026-03-27T12:00:30Z")),
            NullLogger<DependencyHealthService>.Instance);

        var snapshot = await service.GetSnapshotAsync(CancellationToken.None);

        var successFactorsProbe = Assert.Single(snapshot.Probes, probe => probe.Dependency == "SuccessFactors");
        Assert.Equal(DependencyHealthStates.Unhealthy, successFactorsProbe.Status);
        Assert.Contains("HTTP 500", successFactorsProbe.Summary, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("mock string error", successFactorsProbe.Details, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task GetSnapshotAsync_KeepsWorkerHealthy_WhenRunningHeartbeatIsBrieflyStale()
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
                State: "Running",
                Activity: "Executing queued run req-1.",
                StartedAt: DateTimeOffset.Parse("2026-03-27T12:00:00Z"),
                LastSeenAt: DateTimeOffset.Parse("2026-03-27T12:00:00Z")));

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
                "prehireOu": "OU=Prehire,DC=example,DC=com",
                "graveyardOu": "OU=Graveyard,DC=example,DC=com",
                "identityAttribute": "employeeID"
              },
              "successFactors": {
                "baseUrl": "https://example.invalid/odata/v2",
                "query": {
                  "entitySet": "EmpJob",
                  "identityField": "userId",
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
            new FakeTimeProvider(DateTimeOffset.Parse("2026-03-27T12:01:30Z")),
            NullLogger<DependencyHealthService>.Instance);

        var snapshot = await service.GetSnapshotAsync(CancellationToken.None);

        var workerProbe = Assert.Single(snapshot.Probes, probe => probe.Dependency == "Worker Service");
        Assert.Equal(DependencyHealthStates.Healthy, workerProbe.Status);
        Assert.Contains("actively processing a run", workerProbe.Summary, StringComparison.OrdinalIgnoreCase);
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

    private sealed class RetryOnInvalidPropertyMessageHandler : HttpMessageHandler
    {
        public List<string> RequestUris { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            _ = cancellationToken;
            RequestUris.Add(request.RequestUri!.ToString());

            if (RequestUris.Count == 1)
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.BadRequest)
                {
                    Content = new StringContent(
                        """
                        {
                          "error": {
                            "code": "COE_PROPERTY_NOT_FOUND",
                            "message": {
                              "lang": "en-US",
                              "value": "[COE0021]Invalid property names: EmpJob/personIdExternal."
                            }
                          }
                        }
                        """)
                });
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""{"d":{"results":[{"userId":"10001"}]}}""")
            });
        }
    }

    private sealed class JsonStringErrorMessageHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            _ = request;
            _ = cancellationToken;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.InternalServerError)
            {
                Content = new StringContent("\"mock string error\"")
            });
        }
    }

    private sealed class SuccessMessageHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            _ = request;
            _ = cancellationToken;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""{"d":{"results":[{"userId":"10001"}]}}""")
            });
        }
    }

    private sealed class FakeTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => utcNow;
    }
}

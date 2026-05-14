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

        var originalEnvironment = CaptureEnvironment();

        try
        {
            SetEnvironment("Development");

            var service = new DependencyHealthService(
                configLoader,
                pathResolver,
                heartbeatStore,
                new HttpClient(new SuccessMessageHandler()),
                new FakeTimeProvider(DateTimeOffset.Parse("2026-03-27T12:00:30Z")),
                NullLogger<DependencyHealthService>.Instance,
                activeDirectoryProbe: (_, _) => Task.FromResult(("ldaps", "ldap", true, 3, 0, (string?)null)));

            var snapshot = await service.GetSnapshotAsync(CancellationToken.None);

            var activeDirectoryProbe = Assert.Single(snapshot.Probes, probe => probe.Dependency == "Active Directory");
            Assert.Equal(DependencyHealthStates.Healthy, activeDirectoryProbe.Status);
            Assert.Contains("fallback LDAP", activeDirectoryProbe.Summary, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("Requested transport 'ldaps' failed", activeDirectoryProbe.Details, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            RestoreEnvironment(originalEnvironment);
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

        var originalEnvironment = CaptureEnvironment();

        try
        {
            SetEnvironment("Production");

            var service = new DependencyHealthService(
                configLoader,
                pathResolver,
                heartbeatStore,
                new HttpClient(new SuccessMessageHandler()),
                new FakeTimeProvider(DateTimeOffset.Parse("2026-03-27T12:00:30Z")),
                NullLogger<DependencyHealthService>.Instance,
                activeDirectoryProbe: (_, _) => Task.FromResult(("ldaps", "ldap", true, 3, 0, (string?)null)));

            var snapshot = await service.GetSnapshotAsync(CancellationToken.None);

            var activeDirectoryProbe = Assert.Single(snapshot.Probes, probe => probe.Dependency == "Active Directory");
            Assert.Equal(DependencyHealthStates.Healthy, activeDirectoryProbe.Status);
            Assert.Equal("LDAP bind and lookup probe succeeded.", activeDirectoryProbe.Summary);
            Assert.Null(activeDirectoryProbe.Details);
        }
        finally
        {
            RestoreEnvironment(originalEnvironment);
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
                return ("ldaps", "ldaps", false, 0, 0, (string?)null);
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

    [Fact]
    public async Task GetSnapshotAsync_UsesOAuthBearerTokenForSuccessFactorsProbe()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "syncfactors-health-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);

        try
        {
            var (configLoader, pathResolver) = await CreateHealthConfigAsync(tempRoot, authMode: "oauth");
            var handler = new OAuthSuccessMessageHandler();
            var service = new DependencyHealthService(
                configLoader,
                pathResolver,
                new StubWorkerHeartbeatStore(CreateHeartbeat(DateTimeOffset.Parse("2026-03-27T12:00:15Z"))),
                new HttpClient(handler),
                new FakeTimeProvider(DateTimeOffset.Parse("2026-03-27T12:00:30Z")),
                NullLogger<DependencyHealthService>.Instance,
                activeDirectoryProbe: (_, _) => Task.FromResult(("ldaps", "ldaps", false, 1, 0, (string?)null)));

            var snapshot = await service.GetSnapshotAsync(CancellationToken.None);

            var successFactorsProbe = Assert.Single(snapshot.Probes, probe => probe.Dependency == "SuccessFactors");
            Assert.Equal(DependencyHealthStates.Healthy, successFactorsProbe.Status);
            Assert.Equal("Bearer probe-token", handler.SuccessFactorsAuthorization);
            Assert.Contains("company_id=company-1", handler.TokenRequestBody, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public async Task GetSnapshotAsync_ReturnsInvalidJsonProbe_WhenSuccessFactorsReadReturnsNonJson()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "syncfactors-health-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);

        try
        {
            var (configLoader, pathResolver) = await CreateHealthConfigAsync(tempRoot);
            var service = new DependencyHealthService(
                configLoader,
                pathResolver,
                new StubWorkerHeartbeatStore(CreateHeartbeat(DateTimeOffset.Parse("2026-03-27T12:00:15Z"))),
                new HttpClient(new InvalidJsonSuccessMessageHandler()),
                new FakeTimeProvider(DateTimeOffset.Parse("2026-03-27T12:00:30Z")),
                NullLogger<DependencyHealthService>.Instance,
                activeDirectoryProbe: (_, _) => Task.FromResult(("ldaps", "ldaps", false, 1, 0, (string?)null)));

            var snapshot = await service.GetSnapshotAsync(CancellationToken.None);

            var successFactorsProbe = Assert.Single(snapshot.Probes, probe => probe.Dependency == "SuccessFactors");
            Assert.Equal(DependencyHealthStates.Unhealthy, successFactorsProbe.Status);
            Assert.Equal("Read probe returned invalid JSON.", successFactorsProbe.Summary);
            Assert.Contains("not-json", successFactorsProbe.Details, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public async Task GetSnapshotAsync_ReportsWorkerHeartbeatMissingDegradedAndUnhealthyStates()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "syncfactors-health-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);

        try
        {
            var (configLoader, pathResolver) = await CreateHealthConfigAsync(tempRoot);
            var checkedAt = DateTimeOffset.Parse("2026-03-27T12:03:00Z");

            var missingSnapshot = await CreateHealthService(
                configLoader,
                pathResolver,
                new NullWorkerHeartbeatStore(),
                checkedAt).GetSnapshotAsync(CancellationToken.None);
            var missingProbe = Assert.Single(missingSnapshot.Probes, probe => probe.Dependency == "Worker Service");
            Assert.Equal(DependencyHealthStates.Unhealthy, missingProbe.Status);
            Assert.Equal("No worker heartbeat has been recorded.", missingProbe.Summary);

            var degradedSnapshot = await CreateHealthService(
                configLoader,
                pathResolver,
                new StubWorkerHeartbeatStore(CreateHeartbeat(checkedAt.AddSeconds(-75), state: "Idle")),
                checkedAt).GetSnapshotAsync(CancellationToken.None);
            var degradedProbe = Assert.Single(degradedSnapshot.Probes, probe => probe.Dependency == "Worker Service");
            Assert.Equal(DependencyHealthStates.Degraded, degradedProbe.Status);
            Assert.True(degradedProbe.IsStale);

            var unhealthySnapshot = await CreateHealthService(
                configLoader,
                pathResolver,
                new StubWorkerHeartbeatStore(CreateHeartbeat(checkedAt.AddMinutes(-5), state: "Idle")),
                checkedAt).GetSnapshotAsync(CancellationToken.None);
            var unhealthyProbe = Assert.Single(unhealthySnapshot.Probes, probe => probe.Dependency == "Worker Service");
            Assert.Equal(DependencyHealthStates.Unhealthy, unhealthyProbe.Status);
            Assert.True(unhealthyProbe.IsStale);
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public async Task GetSnapshotAsync_ReturnsDegradedActiveDirectoryProbe_WhenSomeSearchBasesAreSkipped()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "syncfactors-health-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);

        try
        {
            var (configLoader, pathResolver) = await CreateHealthConfigAsync(tempRoot);
            var service = new DependencyHealthService(
                configLoader,
                pathResolver,
                new StubWorkerHeartbeatStore(CreateHeartbeat(DateTimeOffset.Parse("2026-03-27T12:00:15Z"))),
                new HttpClient(new SuccessMessageHandler()),
                new FakeTimeProvider(DateTimeOffset.Parse("2026-03-27T12:00:30Z")),
                NullLogger<DependencyHealthService>.Instance,
                activeDirectoryProbe: (_, _) => Task.FromResult((
                    RequestedTransport: "ldaps",
                    EffectiveTransport: "ldaps",
                    UsedFallback: false,
                    SuccessfulBaseCount: 2,
                    SkippedBaseCount: 1,
                    SkippedBaseDetails: (string?)"Skipped search bases: OU=Missing,DC=example,DC=com (search base was not found)")));

            var snapshot = await service.GetSnapshotAsync(CancellationToken.None);

            var activeDirectoryProbe = Assert.Single(snapshot.Probes, probe => probe.Dependency == "Active Directory");
            Assert.Equal(DependencyHealthStates.Degraded, activeDirectoryProbe.Status);
            Assert.Contains("1 search base was skipped", activeDirectoryProbe.Summary, StringComparison.Ordinal);
            Assert.Contains("OU=Missing", activeDirectoryProbe.Details, StringComparison.Ordinal);
            Assert.Equal(DependencyHealthStates.Degraded, snapshot.Status);
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    private static DependencyHealthService CreateHealthService(
        SyncFactorsConfigurationLoader configLoader,
        SqlitePathResolver pathResolver,
        IWorkerHeartbeatStore heartbeatStore,
        DateTimeOffset checkedAt)
    {
        return new DependencyHealthService(
            configLoader,
            pathResolver,
            heartbeatStore,
            new HttpClient(new SuccessMessageHandler()),
            new FakeTimeProvider(checkedAt),
            NullLogger<DependencyHealthService>.Instance,
            activeDirectoryProbe: (_, _) => Task.FromResult(("ldaps", "ldaps", false, 1, 0, (string?)null)));
    }

    private static async Task<(SyncFactorsConfigurationLoader ConfigLoader, SqlitePathResolver PathResolver)> CreateHealthConfigAsync(
        string tempRoot,
        string authMode = "basic")
    {
        var databasePath = Path.Combine(tempRoot, "runtime.db");
        var pathResolver = new SqlitePathResolver(databasePath);
        var initializer = new SqliteDatabaseInitializer(pathResolver);
        await initializer.InitializeAsync(CancellationToken.None);

        var configPath = Path.Combine(tempRoot, "sync-config.json");
        var authJson = string.Equals(authMode, "oauth", StringComparison.OrdinalIgnoreCase)
            ? """
                  "mode": "oauth",
                  "oauth": {
                    "tokenUrl": "https://example.invalid/oauth/token",
                    "clientId": "client-1",
                    "clientSecret": "secret-1",
                    "companyId": "company-1"
                  }
              """
            : """
                  "mode": "basic",
                  "basic": {
                    "username": "user",
                    "password": "pass"
                  }
              """;

        await File.WriteAllTextAsync(
            configPath,
            $$"""
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
                  "entitySet": "EmpJob",
                  "identityField": "userId",
                  "deltaField": "lastModifiedDateTime",
                  "select": [ "userId", "firstName" ],
                  "expand": []
                },
                "auth": {
            {{authJson}}
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

        return (
            new SyncFactorsConfigurationLoader(new SyncFactorsConfigPathResolver(configPath, null)),
            pathResolver);
    }

    private static WorkerHeartbeat CreateHeartbeat(DateTimeOffset lastSeenAt, string state = "Idle")
    {
        return new WorkerHeartbeat(
            Service: "SyncFactors.Worker",
            State: state,
            Activity: "Waiting for scheduled work.",
            StartedAt: DateTimeOffset.Parse("2026-03-27T12:00:00Z"),
            LastSeenAt: lastSeenAt);
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

    private sealed class NullWorkerHeartbeatStore : IWorkerHeartbeatStore
    {
        public Task<WorkerHeartbeat?> GetCurrentAsync(CancellationToken cancellationToken)
        {
            _ = cancellationToken;
            return Task.FromResult<WorkerHeartbeat?>(null);
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

    private sealed class OAuthSuccessMessageHandler : HttpMessageHandler
    {
        public string? TokenRequestBody { get; private set; }
        public string? SuccessFactorsAuthorization { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            _ = cancellationToken;
            if (request.Method == HttpMethod.Post)
            {
                TokenRequestBody = request.Content is null
                    ? null
                    : await request.Content.ReadAsStringAsync(cancellationToken);
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("""{"access_token":"probe-token"}""")
                };
            }

            SuccessFactorsAuthorization = request.Headers.Authorization?.ToString();
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""{"d":{"results":[{"userId":"10001"}]}}""")
            };
        }
    }

    private sealed class InvalidJsonSuccessMessageHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            _ = request;
            _ = cancellationToken;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("not-json")
            });
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

    private static (string? AspNetCoreEnvironment, string? DotNetEnvironment) CaptureEnvironment() =>
        (
            Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT"),
            Environment.GetEnvironmentVariable("DOTNET_ENVIRONMENT")
        );

    private static void SetEnvironment(string environmentName)
    {
        Environment.SetEnvironmentVariable("ASPNETCORE_ENVIRONMENT", environmentName);
        Environment.SetEnvironmentVariable("DOTNET_ENVIRONMENT", environmentName);
    }

    private static void RestoreEnvironment((string? AspNetCoreEnvironment, string? DotNetEnvironment) original)
    {
        Environment.SetEnvironmentVariable("ASPNETCORE_ENVIRONMENT", original.AspNetCoreEnvironment);
        Environment.SetEnvironmentVariable("DOTNET_ENVIRONMENT", original.DotNetEnvironment);
    }
}

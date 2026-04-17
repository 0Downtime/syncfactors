using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using SyncFactors.Api;
using SyncFactors.Contracts;
using SyncFactors.Domain;

namespace SyncFactors.Api.Tests;

public sealed class DashboardRealtimeServiceTests
{
    [Fact]
    public async Task ExecuteAsync_DoesNotStopHostWhenDashboardSnapshotPublishFails()
    {
        var tracker = new DashboardRealtimeConnectionTracker();
        tracker.Increment();

        var hubContext = new StubHubContext();
        var service = new DashboardRealtimeService(
            new StubServiceScopeFactory(
                new ThrowingDashboardSnapshotService(),
                new HealthyDependencyHealthService()),
            hubContext,
            tracker,
            new DashboardOptions(HealthProbesEnabled: true),
            new FixedTimeProvider(DateTimeOffset.Parse("2026-04-10T12:00:00Z")),
            NullLogger<DashboardRealtimeService>.Instance);

        await service.StartAsync(CancellationToken.None);
        await Task.Delay(TimeSpan.FromMilliseconds(2600));

        var exception = await Record.ExceptionAsync(() => service.StopAsync(CancellationToken.None));

        Assert.Null(exception);
        Assert.DoesNotContain(
            hubContext.Client.ReceivedMessages,
            message => string.Equals(message.Type, DashboardRealtimeEventTypes.DashboardSnapshotUpdated, StringComparison.Ordinal));
    }

    [Fact]
    public async Task ExecuteAsync_DoesNotStopHostWhenHealthSnapshotPublishFails()
    {
        var tracker = new DashboardRealtimeConnectionTracker();
        tracker.Increment();

        var hubContext = new StubHubContext();
        var service = new DashboardRealtimeService(
            new StubServiceScopeFactory(
                new HealthyDashboardSnapshotService(),
                new ThrowingDependencyHealthService()),
            hubContext,
            tracker,
            new DashboardOptions(HealthProbesEnabled: true),
            new FixedTimeProvider(DateTimeOffset.Parse("2026-04-10T12:00:00Z")),
            NullLogger<DashboardRealtimeService>.Instance);

        await service.StartAsync(CancellationToken.None);
        await Task.Delay(TimeSpan.FromMilliseconds(2600));

        var exception = await Record.ExceptionAsync(() => service.StopAsync(CancellationToken.None));

        Assert.Null(exception);
        Assert.Contains(
            hubContext.Client.ReceivedMessages,
            message => string.Equals(message.Type, DashboardRealtimeEventTypes.DashboardSnapshotUpdated, StringComparison.Ordinal));
    }

    [Fact]
    public async Task ExecuteAsync_DoesNotPublishHealthSnapshotsWhenDashboardHealthProbesAreDisabled()
    {
        var tracker = new DashboardRealtimeConnectionTracker();
        tracker.Increment();

        var hubContext = new StubHubContext();
        var service = new DashboardRealtimeService(
            new StubServiceScopeFactory(
                new HealthyDashboardSnapshotService(),
                new ThrowingDependencyHealthService()),
            hubContext,
            tracker,
            new DashboardOptions(HealthProbesEnabled: false),
            new FixedTimeProvider(DateTimeOffset.Parse("2026-04-10T12:00:00Z")),
            NullLogger<DashboardRealtimeService>.Instance);

        await service.StartAsync(CancellationToken.None);
        await Task.Delay(TimeSpan.FromMilliseconds(2600));
        await service.StopAsync(CancellationToken.None);

        Assert.Contains(
            hubContext.Client.ReceivedMessages,
            message => string.Equals(message.Type, DashboardRealtimeEventTypes.DashboardSnapshotUpdated, StringComparison.Ordinal));
        Assert.DoesNotContain(
            hubContext.Client.ReceivedMessages,
            message => string.Equals(message.Type, DashboardRealtimeEventTypes.HealthSnapshotUpdated, StringComparison.Ordinal));
    }

    private sealed class StubServiceScopeFactory(
        IDashboardSnapshotService dashboardSnapshotService,
        IDependencyHealthService dependencyHealthService) : IServiceScopeFactory
    {
        public IServiceScope CreateScope()
        {
            return new StubServiceScope(
                new Dictionary<Type, object>
                {
                    [typeof(IDashboardSnapshotService)] = dashboardSnapshotService,
                    [typeof(IDependencyHealthService)] = dependencyHealthService
                });
        }
    }

    private sealed class StubServiceScope(IReadOnlyDictionary<Type, object> services) : IServiceScope
    {
        public IServiceProvider ServiceProvider { get; } = new StubServiceProvider(services);

        public void Dispose()
        {
        }
    }

    private sealed class StubServiceProvider(IReadOnlyDictionary<Type, object> services) : IServiceProvider
    {
        public object? GetService(Type serviceType)
        {
            return services.TryGetValue(serviceType, out var service)
                ? service
                : null;
        }
    }

    private sealed class StubHubContext : IHubContext<DashboardHub, IDashboardRealtimeClient>
    {
        public StubRealtimeClient Client { get; } = new();

        public IHubClients<IDashboardRealtimeClient> Clients { get; }
            = new StubHubClients();

        public IGroupManager Groups { get; } = new StubGroupManager();

        public StubHubContext()
        {
            Clients = new StubHubClients(Client);
        }
    }

    private sealed class StubHubClients(StubRealtimeClient? client = null) : IHubClients<IDashboardRealtimeClient>
    {
        private readonly IDashboardRealtimeClient _client = client ?? new StubRealtimeClient();

        public IDashboardRealtimeClient All => _client;

        public IDashboardRealtimeClient AllExcept(IReadOnlyList<string> excludedConnectionIds) => _client;

        public IDashboardRealtimeClient Client(string connectionId) => _client;

        public IDashboardRealtimeClient Clients(IReadOnlyList<string> connectionIds) => _client;

        public IDashboardRealtimeClient Group(string groupName) => _client;

        public IDashboardRealtimeClient GroupExcept(string groupName, IReadOnlyList<string> excludedConnectionIds) => _client;

        public IDashboardRealtimeClient Groups(IReadOnlyList<string> groupNames) => _client;

        public IDashboardRealtimeClient User(string userId) => _client;

        public IDashboardRealtimeClient Users(IReadOnlyList<string> userIds) => _client;
    }

    private sealed class StubGroupManager : IGroupManager
    {
        public Task AddToGroupAsync(string connectionId, string groupName, CancellationToken cancellationToken = default)
        {
            return Task.CompletedTask;
        }

        public Task RemoveFromGroupAsync(string connectionId, string groupName, CancellationToken cancellationToken = default)
        {
            return Task.CompletedTask;
        }
    }

    private sealed class StubRealtimeClient : IDashboardRealtimeClient
    {
        public List<DashboardRealtimeEvent> ReceivedMessages { get; } = [];

        public Task DashboardEvent(DashboardRealtimeEvent message)
        {
            ReceivedMessages.Add(message);
            return Task.CompletedTask;
        }
    }

    private sealed class HealthyDashboardSnapshotService : IDashboardSnapshotService
    {
        public Task<DashboardSnapshot> GetSnapshotAsync(CancellationToken cancellationToken)
        {
            _ = cancellationToken;
            return Task.FromResult(
                new DashboardSnapshot(
                    Status: new RuntimeStatus("Idle", "NotStarted", null, null, true, 0, 0, null, null, null, null, null, null),
                    Runs: [],
                    ActiveRun: null,
                    LastCompletedRun: null,
                    RequiresAttention: false,
                    AttentionMessage: null,
                    CheckedAt: DateTimeOffset.Parse("2026-04-10T12:00:00Z")));
        }
    }

    private sealed class ThrowingDashboardSnapshotService : IDashboardSnapshotService
    {
        public Task<DashboardSnapshot> GetSnapshotAsync(CancellationToken cancellationToken)
        {
            _ = cancellationToken;
            throw new InvalidOperationException("dashboard unavailable");
        }
    }

    private sealed class HealthyDependencyHealthService : IDependencyHealthService
    {
        public Task<DependencyHealthSnapshot> GetSnapshotAsync(CancellationToken cancellationToken)
        {
            _ = cancellationToken;
            return Task.FromResult(
                new DependencyHealthSnapshot(
                    DependencyHealthStates.Healthy,
                    DateTimeOffset.Parse("2026-04-10T12:00:00Z"),
                    []));
        }
    }

    private sealed class ThrowingDependencyHealthService : IDependencyHealthService
    {
        public Task<DependencyHealthSnapshot> GetSnapshotAsync(CancellationToken cancellationToken)
        {
            _ = cancellationToken;
            throw new InvalidOperationException("health unavailable");
        }
    }

    private sealed class FixedTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => utcNow;
    }
}

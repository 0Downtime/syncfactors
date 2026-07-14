using Microsoft.Extensions.Logging.Abstractions;
using SyncFactors.Contracts;
using SyncFactors.Domain;
using SyncFactors.Worker;
using WorkerService = SyncFactors.Worker.Worker;

namespace SyncFactors.Api.Tests;

public sealed class WorkerHostTests
{
    [Fact]
    public async Task ExecuteAsync_WritesStartingHeartbeatBeforeHostCancellation()
    {
        var heartbeatStore = new CapturingHeartbeatStore();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var worker = new TestWorker(
            NullLogger<WorkerService>.Instance,
            new NoopRunQueueStore(),
            null!,
            null!,
            null!,
            null!,
            null!,
            heartbeatStore,
            TimeProvider.System,
            null!);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => worker.RunAsync(cancellation.Token));

        Assert.Collection(
            heartbeatStore.Heartbeats,
            heartbeat => Assert.Equal("Starting", heartbeat.State));
    }

    private sealed class TestWorker : WorkerService
    {
        public TestWorker(
            Microsoft.Extensions.Logging.ILogger<WorkerService> logger,
            IRunQueueStore runQueueStore,
            SyncScheduleCoordinator syncScheduleCoordinator,
            GraveyardRetentionReportCoordinator graveyardRetentionReportCoordinator,
            GraveyardAutoDeleteCoordinator graveyardAutoDeleteCoordinator,
            BulkRunCoordinator bulkRunCoordinator,
            DeleteAllUsersCoordinator deleteAllUsersCoordinator,
            IWorkerHeartbeatStore workerHeartbeatStore,
            TimeProvider timeProvider,
            SyncFactors.Infrastructure.SyncFactorsConfigurationLoader configLoader)
            : base(logger, runQueueStore, syncScheduleCoordinator, graveyardRetentionReportCoordinator, graveyardAutoDeleteCoordinator, bulkRunCoordinator, deleteAllUsersCoordinator, workerHeartbeatStore, timeProvider, configLoader)
        {
        }

        public Task RunAsync(CancellationToken cancellationToken) => ExecuteAsync(cancellationToken);
    }

    private sealed class CapturingHeartbeatStore : IWorkerHeartbeatStore
    {
        public List<WorkerHeartbeat> Heartbeats { get; } = [];

        public Task SaveAsync(WorkerHeartbeat heartbeat, CancellationToken cancellationToken)
        {
            Heartbeats.Add(heartbeat);
            cancellationToken.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        }

        public Task<WorkerHeartbeat?> GetCurrentAsync(CancellationToken cancellationToken) => Task.FromResult<WorkerHeartbeat?>(null);
    }

    private sealed class NoopRunQueueStore : IRunQueueStore
    {
        public Task<RunQueueRequest> EnqueueAsync(StartRunRequest request, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<RunQueueRequest?> ClaimNextPendingAsync(string workerName, CancellationToken cancellationToken) => Task.FromResult<RunQueueRequest?>(null);
        public Task<RunQueueRequest?> GetAsync(string requestId, CancellationToken cancellationToken) => Task.FromResult<RunQueueRequest?>(null);
        public Task<RunQueueRequest?> GetPendingOrActiveAsync(CancellationToken cancellationToken) => Task.FromResult<RunQueueRequest?>(null);
        public Task<bool> HasPendingOrActiveRunAsync(CancellationToken cancellationToken) => Task.FromResult(false);
        public Task<bool> CancelPendingOrActiveAsync(string? requestedBy, CancellationToken cancellationToken) => Task.FromResult(false);
        public Task<bool> IsCancellationRequestedAsync(string requestId, CancellationToken cancellationToken) => Task.FromResult(false);
        public Task CompleteAsync(string requestId, string runId, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task CancelAsync(string requestId, string? runId, string? errorMessage, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task FailAsync(string requestId, string? runId, string errorMessage, CancellationToken cancellationToken) => Task.CompletedTask;
    }
}

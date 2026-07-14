using Microsoft.Extensions.Logging.Abstractions;
using SyncFactors.Contracts;
using SyncFactors.Domain;
using SyncFactors.Worker;
using WorkerService = SyncFactors.Worker.Worker;

namespace SyncFactors.Api.Tests;

public sealed class WorkerHostTests
{
    [Fact]
    public async Task ExecuteAsync_DispatchesClaimedRun_InvokesMaintenanceAndPersistsShutdownHeartbeats()
    {
        var heartbeatStore = new CapturingHeartbeatStore();
        using var cancellation = new CancellationTokenSource();
        var queueStore = new CapturingRunQueueStore(
            new RunQueueRequest(
                RequestId: "request-1",
                Mode: "BulkSync",
                DryRun: true,
                RunTrigger: "Scheduled",
                RequestedBy: "test",
                Status: "Active",
                RequestedAt: DateTimeOffset.UtcNow,
                StartedAt: DateTimeOffset.UtcNow,
                CompletedAt: null,
                RunId: null,
                ErrorMessage: null),
            cancellation);
        var scheduleCoordinator = new CapturingScheduleCoordinator();
        var retentionReportCoordinator = new CapturingRetentionReportCoordinator();
        var autoDeleteCoordinator = new CapturingAutoDeleteCoordinator();
        var bulkRunCoordinator = new CapturingBulkRunCoordinator();
        var worker = new TestWorker(
            NullLogger<WorkerService>.Instance,
            queueStore,
            scheduleCoordinator,
            retentionReportCoordinator,
            autoDeleteCoordinator,
            bulkRunCoordinator,
            new NoopDeleteAllUsersCoordinator(),
            heartbeatStore,
            TimeProvider.System,
            new FixedWorkerExecutionSettings(3));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => worker.RunAsync(cancellation.Token));

        Assert.Equal(1, scheduleCoordinator.Calls);
        Assert.Equal(1, retentionReportCoordinator.Calls);
        Assert.Equal(0, autoDeleteCoordinator.Calls);
        Assert.Equal("request-1", bulkRunCoordinator.RequestId);
        Assert.Equal(3, bulkRunCoordinator.MaxDegreeOfParallelism);
        Assert.Equal([("request-1", "run-1")], queueStore.CompletedRuns);
        Assert.Equal(["Starting", "Idle", "Running", "Idle"], heartbeatStore.Heartbeats.Select(heartbeat => heartbeat.State));
    }

    [Fact]
    public async Task ExecuteAsync_InvokesAutoDeleteWhenNoRunIsClaimed()
    {
        using var cancellation = new CancellationTokenSource();
        var autoDeleteCoordinator = new CapturingAutoDeleteCoordinator(cancellation);
        var worker = new TestWorker(
            NullLogger<WorkerService>.Instance,
            new CapturingRunQueueStore(null, cancellation),
            new CapturingScheduleCoordinator(),
            new CapturingRetentionReportCoordinator(),
            autoDeleteCoordinator,
            new CapturingBulkRunCoordinator(),
            new NoopDeleteAllUsersCoordinator(),
            new CapturingHeartbeatStore(),
            TimeProvider.System,
            new FixedWorkerExecutionSettings(1));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => worker.RunAsync(cancellation.Token));

        Assert.Equal(1, autoDeleteCoordinator.Calls);
    }

    [Fact]
    public async Task ExecuteAsync_DispatchesDeleteAllUsersRequestToDeleteCoordinator()
    {
        using var cancellation = new CancellationTokenSource();
        var deleteCoordinator = new CapturingDeleteAllUsersCoordinator();
        var worker = new TestWorker(
            NullLogger<WorkerService>.Instance,
            new CapturingRunQueueStore(CreateRequest("DeleteAllUsers"), cancellation),
            new CapturingScheduleCoordinator(),
            new CapturingRetentionReportCoordinator(),
            new CapturingAutoDeleteCoordinator(),
            new CapturingBulkRunCoordinator(),
            deleteCoordinator,
            new CapturingHeartbeatStore(),
            TimeProvider.System,
            new FixedWorkerExecutionSettings(1));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => worker.RunAsync(cancellation.Token));

        Assert.Equal("request-1", deleteCoordinator.RequestId);
    }

    private static RunQueueRequest CreateRequest(string mode = "BulkSync") => new(
        RequestId: "request-1",
        Mode: mode,
        DryRun: true,
        RunTrigger: "Scheduled",
        RequestedBy: "test",
        Status: "Active",
        RequestedAt: DateTimeOffset.UtcNow,
        StartedAt: DateTimeOffset.UtcNow,
        CompletedAt: null,
        RunId: null,
        ErrorMessage: null);

    private sealed class TestWorker : WorkerService
    {
        public TestWorker(
            Microsoft.Extensions.Logging.ILogger<WorkerService> logger,
            IRunQueueStore runQueueStore,
            ISyncScheduleCoordinator syncScheduleCoordinator,
            IGraveyardRetentionReportCoordinator graveyardRetentionReportCoordinator,
            IGraveyardAutoDeleteCoordinator graveyardAutoDeleteCoordinator,
            IBulkRunCoordinator bulkRunCoordinator,
            IDeleteAllUsersCoordinator deleteAllUsersCoordinator,
            IWorkerHeartbeatStore workerHeartbeatStore,
            TimeProvider timeProvider,
            IWorkerExecutionSettings executionSettings)
            : base(logger, runQueueStore, syncScheduleCoordinator, graveyardRetentionReportCoordinator, graveyardAutoDeleteCoordinator, bulkRunCoordinator, deleteAllUsersCoordinator, workerHeartbeatStore, timeProvider, executionSettings)
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

    private sealed class CapturingRunQueueStore(RunQueueRequest? request, CancellationTokenSource cancellation) : IRunQueueStore
    {
        public Task<RunQueueRequest> EnqueueAsync(StartRunRequest request, CancellationToken cancellationToken) => throw new NotSupportedException();
        public List<(string RequestId, string RunId)> CompletedRuns { get; } = [];
        private bool _claimed;
        public Task<RunQueueRequest?> ClaimNextPendingAsync(string workerName, CancellationToken cancellationToken)
        {
            if (_claimed)
            {
                return Task.FromResult<RunQueueRequest?>(null);
            }

            _claimed = true;
            return Task.FromResult<RunQueueRequest?>(request);
        }
        public Task<RunQueueRequest?> GetAsync(string requestId, CancellationToken cancellationToken) => Task.FromResult<RunQueueRequest?>(null);
        public Task<RunQueueRequest?> GetPendingOrActiveAsync(CancellationToken cancellationToken) => Task.FromResult<RunQueueRequest?>(null);
        public Task<bool> HasPendingOrActiveRunAsync(CancellationToken cancellationToken) => Task.FromResult(false);
        public Task<bool> CancelPendingOrActiveAsync(string? requestedBy, CancellationToken cancellationToken) => Task.FromResult(false);
        public Task<bool> IsCancellationRequestedAsync(string requestId, CancellationToken cancellationToken) => Task.FromResult(false);
        public Task CompleteAsync(string requestId, string runId, CancellationToken cancellationToken)
        {
            CompletedRuns.Add((requestId, runId));
            cancellation.Cancel();
            return Task.CompletedTask;
        }
        public Task CancelAsync(string requestId, string? runId, string? errorMessage, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task FailAsync(string requestId, string? runId, string errorMessage, CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class CapturingScheduleCoordinator : ISyncScheduleCoordinator
    {
        public int Calls { get; private set; }
        public Task<bool> TryEnqueueDueRunAsync(CancellationToken cancellationToken) { Calls++; return Task.FromResult(false); }
    }

    private sealed class CapturingRetentionReportCoordinator : IGraveyardRetentionReportCoordinator
    {
        public int Calls { get; private set; }
        public Task<bool> TrySendDueReportAsync(CancellationToken cancellationToken) { Calls++; return Task.FromResult(false); }
    }

    private sealed class CapturingAutoDeleteCoordinator(CancellationTokenSource? cancellation = null) : IGraveyardAutoDeleteCoordinator
    {
        public int Calls { get; private set; }
        public Task<string?> TryExecuteAsync(CancellationToken cancellationToken)
        {
            Calls++;
            cancellation?.Cancel();
            return Task.FromResult<string?>(null);
        }
    }

    private sealed class CapturingBulkRunCoordinator : IBulkRunCoordinator
    {
        public string? RequestId { get; private set; }
        public int MaxDegreeOfParallelism { get; private set; }
        public Task<string> ExecuteAsync(RunQueueRequest request, int maxDegreeOfParallelism, CancellationToken cancellationToken)
        {
            RequestId = request.RequestId;
            MaxDegreeOfParallelism = maxDegreeOfParallelism;
            return Task.FromResult("run-1");
        }
    }

    private sealed class NoopDeleteAllUsersCoordinator : IDeleteAllUsersCoordinator
    {
        public Task<string> ExecuteAsync(RunQueueRequest request, CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    private sealed class CapturingDeleteAllUsersCoordinator : IDeleteAllUsersCoordinator
    {
        public string? RequestId { get; private set; }
        public Task<string> ExecuteAsync(RunQueueRequest request, CancellationToken cancellationToken)
        {
            RequestId = request.RequestId;
            return Task.FromResult("delete-run-1");
        }
    }

    private sealed class FixedWorkerExecutionSettings(int maxDegreeOfParallelism) : IWorkerExecutionSettings
    {
        public int GetMaxDegreeOfParallelism() => maxDegreeOfParallelism;
    }
}

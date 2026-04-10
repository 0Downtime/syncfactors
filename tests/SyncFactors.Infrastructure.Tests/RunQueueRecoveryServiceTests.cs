using Microsoft.Extensions.Logging.Abstractions;
using SyncFactors.Contracts;
using SyncFactors.Domain;
using SyncFactors.Infrastructure;
using System.Text.Json;

namespace SyncFactors.Infrastructure.Tests;

public sealed class RunQueueRecoveryServiceTests
{
    [Fact]
    public async Task RecoverIfNeededAsync_MarksStaleInProgressRunAsFailed()
    {
        var now = DateTimeOffset.Parse("2026-04-09T14:00:00Z");
        var queueStore = new StubRunQueueStore(
            new RunQueueRequest(
                RequestId: "req-1",
                Mode: "BulkSync",
                DryRun: false,
                RunTrigger: "AdHoc",
                RequestedBy: "test",
                Status: "InProgress",
                RequestedAt: now.AddMinutes(-10),
                StartedAt: now.AddMinutes(-9),
                CompletedAt: null,
                RunId: "run-1",
                ErrorMessage: null),
            recoverResult: 1);
        var runtimeStatusStore = new StubRuntimeStatusStore(
            new RuntimeStatus(
                Status: "InProgress",
                Stage: "BulkSync",
                RunId: "run-1",
                Mode: "BulkSync",
                DryRun: false,
                ProcessedWorkers: 12,
                TotalWorkers: 50,
                CurrentWorkerId: "10001",
                LastAction: "Processing 10001",
                StartedAt: now.AddMinutes(-9),
                LastUpdatedAt: now.AddMinutes(-8),
                CompletedAt: null,
                ErrorMessage: null));
        var runRepository = new StubRunRepository(CreateRunDetail("run-1", "InProgress", now.AddMinutes(-9)));
        var heartbeatStore = new StubWorkerHeartbeatStore(
            new WorkerHeartbeat(
                Service: "SyncFactors.Worker",
                State: "Running",
                Activity: "Executing queued run req-1.",
                StartedAt: now.AddMinutes(-9),
                LastSeenAt: now.AddMinutes(-5)));
        var service = new RunQueueRecoveryService(
            queueStore,
            runtimeStatusStore,
            runRepository,
            heartbeatStore,
            new FixedTimeProvider(now),
            NullLogger<RunQueueRecoveryService>.Instance);

        var recovered = await service.RecoverIfNeededAsync("worker startup", CancellationToken.None);

        Assert.Equal(1, recovered);
        Assert.Equal(1, queueStore.RecoverCalls);
        Assert.NotNull(queueStore.LastRecoveryMessage);
        Assert.Contains("worker startup", queueStore.LastRecoveryMessage, StringComparison.Ordinal);

        var savedRun = Assert.Single(runRepository.SavedRuns);
        Assert.Equal("run-1", savedRun.RunId);
        Assert.Equal("Failed", savedRun.Status);
        Assert.Equal(now, savedRun.CompletedAt);

        var savedRuntime = Assert.Single(runtimeStatusStore.SavedStatuses);
        Assert.Equal("Failed", savedRuntime.Status);
        Assert.Equal("run-1", savedRuntime.RunId);
        Assert.Null(savedRuntime.CurrentWorkerId);
        Assert.Equal(now, savedRuntime.CompletedAt);
        Assert.Contains("Recovered orphaned run during worker startup", savedRuntime.ErrorMessage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RecoverIfNeededAsync_MarksCancelRequestedRunAsCanceled()
    {
        var now = DateTimeOffset.Parse("2026-04-09T14:00:00Z");
        var queueStore = new StubRunQueueStore(
            new RunQueueRequest(
                RequestId: "req-2",
                Mode: "BulkSync",
                DryRun: true,
                RunTrigger: "AdHoc",
                RequestedBy: "test",
                Status: "CancelRequested",
                RequestedAt: now.AddMinutes(-10),
                StartedAt: now.AddMinutes(-9),
                CompletedAt: null,
                RunId: "run-2",
                ErrorMessage: "Cancellation requested."),
            recoverResult: 1);
        var runtimeStatusStore = new StubRuntimeStatusStore(
            new RuntimeStatus(
                Status: "InProgress",
                Stage: "BulkSync",
                RunId: "run-2",
                Mode: "BulkSync",
                DryRun: true,
                ProcessedWorkers: 3,
                TotalWorkers: 20,
                CurrentWorkerId: "10002",
                LastAction: "Cancel requested",
                StartedAt: now.AddMinutes(-9),
                LastUpdatedAt: now.AddMinutes(-8),
                CompletedAt: null,
                ErrorMessage: null));
        var runRepository = new StubRunRepository(CreateRunDetail("run-2", "InProgress", now.AddMinutes(-9), dryRun: true));
        var heartbeatStore = new StubWorkerHeartbeatStore(null);
        var service = new RunQueueRecoveryService(
            queueStore,
            runtimeStatusStore,
            runRepository,
            heartbeatStore,
            new FixedTimeProvider(now),
            NullLogger<RunQueueRecoveryService>.Instance);

        var recovered = await service.RecoverIfNeededAsync("api startup", CancellationToken.None);

        Assert.Equal(1, recovered);

        var savedRun = Assert.Single(runRepository.SavedRuns);
        Assert.Equal("Canceled", savedRun.Status);

        var savedRuntime = Assert.Single(runtimeStatusStore.SavedStatuses);
        Assert.Equal("Idle", savedRuntime.Status);
        Assert.Equal("Canceled", savedRuntime.Stage);
        Assert.Null(savedRuntime.ErrorMessage);
    }

    [Fact]
    public async Task RecoverIfNeededAsync_SkipsWhenRunningHeartbeatIsFresh()
    {
        var now = DateTimeOffset.Parse("2026-04-09T14:00:00Z");
        var queueStore = new StubRunQueueStore(
            new RunQueueRequest(
                RequestId: "req-3",
                Mode: "BulkSync",
                DryRun: false,
                RunTrigger: "AdHoc",
                RequestedBy: "test",
                Status: "InProgress",
                RequestedAt: now.AddMinutes(-3),
                StartedAt: now.AddMinutes(-2),
                CompletedAt: null,
                RunId: "run-3",
                ErrorMessage: null),
            recoverResult: 1);
        var runtimeStatusStore = new StubRuntimeStatusStore(
            new RuntimeStatus(
                Status: "InProgress",
                Stage: "BulkSync",
                RunId: "run-3",
                Mode: "BulkSync",
                DryRun: false,
                ProcessedWorkers: 1,
                TotalWorkers: 10,
                CurrentWorkerId: "10003",
                LastAction: "Processing",
                StartedAt: now.AddMinutes(-2),
                LastUpdatedAt: now.AddMinutes(-1),
                CompletedAt: null,
                ErrorMessage: null));
        var runRepository = new StubRunRepository(CreateRunDetail("run-3", "InProgress", now.AddMinutes(-2)));
        var heartbeatStore = new StubWorkerHeartbeatStore(
            new WorkerHeartbeat(
                Service: "SyncFactors.Worker",
                State: "Running",
                Activity: "Executing queued run req-3.",
                StartedAt: now.AddMinutes(-2),
                LastSeenAt: now.AddSeconds(-30)));
        var service = new RunQueueRecoveryService(
            queueStore,
            runtimeStatusStore,
            runRepository,
            heartbeatStore,
            new FixedTimeProvider(now),
            NullLogger<RunQueueRecoveryService>.Instance);

        var recovered = await service.RecoverIfNeededAsync("worker startup", CancellationToken.None);

        Assert.Equal(0, recovered);
        Assert.Equal(0, queueStore.RecoverCalls);
        Assert.Empty(runRepository.SavedRuns);
        Assert.Empty(runtimeStatusStore.SavedStatuses);
    }

    private static RunDetail CreateRunDetail(string runId, string status, DateTimeOffset startedAt, bool dryRun = false)
    {
        return new RunDetail(
            new RunSummary(
                RunId: runId,
                Path: null,
                ArtifactType: "BulkRun",
                ConfigPath: null,
                MappingConfigPath: null,
                Mode: "BulkSync",
                DryRun: dryRun,
                Status: status,
                StartedAt: startedAt,
                CompletedAt: null,
                DurationSeconds: null,
                ProcessedWorkers: 12,
                TotalWorkers: 50,
                Creates: 1,
                Updates: 2,
                Enables: 0,
                Disables: 0,
                GraveyardMoves: 0,
                Deletions: 0,
                Quarantined: 0,
                Conflicts: 0,
                GuardrailFailures: 0,
                ManualReview: 0,
                Unchanged: 0,
                SyncScope: "Delta"),
            JsonDocument.Parse("""{"kind":"bulkRun"}""").RootElement.Clone(),
            new Dictionary<string, int>());
    }

    private sealed class StubRunQueueStore(RunQueueRequest? current, int recoverResult) : IRunQueueStore
    {
        public int RecoverCalls { get; private set; }

        public string? LastRecoveryMessage { get; private set; }

        public Task<RunQueueRequest> EnqueueAsync(StartRunRequest request, CancellationToken cancellationToken)
        {
            _ = request;
            _ = cancellationToken;
            throw new NotSupportedException();
        }

        public Task<RunQueueRequest?> ClaimNextPendingAsync(string workerName, CancellationToken cancellationToken)
        {
            _ = workerName;
            _ = cancellationToken;
            return Task.FromResult<RunQueueRequest?>(null);
        }

        public Task<RunQueueRequest?> GetPendingOrActiveAsync(CancellationToken cancellationToken)
        {
            _ = cancellationToken;
            return Task.FromResult(current);
        }

        public Task<bool> HasPendingOrActiveRunAsync(CancellationToken cancellationToken)
        {
            _ = cancellationToken;
            return Task.FromResult(current is not null);
        }

        public Task<bool> CancelPendingOrActiveAsync(string? requestedBy, CancellationToken cancellationToken)
        {
            _ = requestedBy;
            _ = cancellationToken;
            throw new NotSupportedException();
        }

        public Task<bool> IsCancellationRequestedAsync(string requestId, CancellationToken cancellationToken)
        {
            _ = requestId;
            _ = cancellationToken;
            return Task.FromResult(false);
        }

        public Task CompleteAsync(string requestId, string runId, CancellationToken cancellationToken)
        {
            _ = requestId;
            _ = runId;
            _ = cancellationToken;
            throw new NotSupportedException();
        }

        public Task CancelAsync(string requestId, string? runId, string? errorMessage, CancellationToken cancellationToken)
        {
            _ = requestId;
            _ = runId;
            _ = errorMessage;
            _ = cancellationToken;
            throw new NotSupportedException();
        }

        public Task FailAsync(string requestId, string? runId, string errorMessage, CancellationToken cancellationToken)
        {
            _ = requestId;
            _ = runId;
            _ = errorMessage;
            _ = cancellationToken;
            throw new NotSupportedException();
        }

        public Task<int> RecoverOrphanedActiveRunsAsync(string? errorMessage, CancellationToken cancellationToken)
        {
            _ = cancellationToken;
            RecoverCalls++;
            LastRecoveryMessage = errorMessage;
            return Task.FromResult(recoverResult);
        }
    }

    private sealed class StubRuntimeStatusStore(RuntimeStatus? current) : IRuntimeStatusStore
    {
        public List<RuntimeStatus> SavedStatuses { get; } = [];

        public Task<RuntimeStatus?> GetCurrentAsync(CancellationToken cancellationToken)
        {
            _ = cancellationToken;
            return Task.FromResult(current);
        }

        public Task<bool> TryStartAsync(RuntimeStatus status, CancellationToken cancellationToken)
        {
            _ = status;
            _ = cancellationToken;
            throw new NotSupportedException();
        }

        public Task SaveAsync(RuntimeStatus status, CancellationToken cancellationToken)
        {
            _ = cancellationToken;
            SavedStatuses.Add(status);
            return Task.CompletedTask;
        }
    }

    private sealed class StubRunRepository(RunDetail? runDetail) : IRunRepository
    {
        public List<RunRecord> SavedRuns { get; } = [];

        public Task<IReadOnlyList<RunSummary>> ListRunsAsync(CancellationToken cancellationToken)
        {
            _ = cancellationToken;
            return Task.FromResult<IReadOnlyList<RunSummary>>([]);
        }

        public Task<RunDetail?> GetRunAsync(string runId, CancellationToken cancellationToken)
        {
            _ = cancellationToken;
            return Task.FromResult(
                runDetail is not null && string.Equals(runDetail.Run.RunId, runId, StringComparison.Ordinal)
                    ? runDetail
                    : null);
        }

        public Task<WorkerPreviewResult?> GetWorkerPreviewAsync(string runId, CancellationToken cancellationToken)
        {
            _ = runId;
            _ = cancellationToken;
            return Task.FromResult<WorkerPreviewResult?>(null);
        }

        public Task<IReadOnlyList<WorkerPreviewHistoryItem>> ListWorkerPreviewHistoryAsync(string workerId, int take, CancellationToken cancellationToken)
        {
            _ = workerId;
            _ = take;
            _ = cancellationToken;
            return Task.FromResult<IReadOnlyList<WorkerPreviewHistoryItem>>([]);
        }

        public Task SaveRunAsync(RunRecord run, CancellationToken cancellationToken)
        {
            _ = cancellationToken;
            SavedRuns.Add(run);
            return Task.CompletedTask;
        }

        public Task ReplaceRunEntriesAsync(string runId, IReadOnlyList<RunEntryRecord> entries, CancellationToken cancellationToken)
        {
            _ = runId;
            _ = entries;
            _ = cancellationToken;
            throw new NotSupportedException();
        }

        public Task AppendRunEntryAsync(RunEntryRecord entry, CancellationToken cancellationToken)
        {
            _ = entry;
            _ = cancellationToken;
            throw new NotSupportedException();
        }

        public Task<IReadOnlyList<RunEntry>> GetRunEntriesAsync(string runId, string? bucket, string? workerId, string? reason, string? filter, string? entryId, int skip, int take, CancellationToken cancellationToken)
        {
            _ = runId;
            _ = bucket;
            _ = workerId;
            _ = reason;
            _ = filter;
            _ = entryId;
            _ = skip;
            _ = take;
            _ = cancellationToken;
            return Task.FromResult<IReadOnlyList<RunEntry>>([]);
        }

        public Task<IReadOnlyList<ChangedAttributeTotal>> GetRunEntryAttributeTotalsAsync(string runId, string? bucket, string? workerId, string? reason, string? filter, string? entryId, CancellationToken cancellationToken)
        {
            _ = runId;
            _ = bucket;
            _ = workerId;
            _ = reason;
            _ = filter;
            _ = entryId;
            _ = cancellationToken;
            return Task.FromResult<IReadOnlyList<ChangedAttributeTotal>>([]);
        }

        public Task<int> CountRunEntriesAsync(string runId, string? bucket, string? workerId, string? reason, string? filter, string? entryId, CancellationToken cancellationToken)
        {
            _ = runId;
            _ = bucket;
            _ = workerId;
            _ = reason;
            _ = filter;
            _ = entryId;
            _ = cancellationToken;
            return Task.FromResult(0);
        }
    }

    private sealed class StubWorkerHeartbeatStore(WorkerHeartbeat? current) : IWorkerHeartbeatStore
    {
        public Task<WorkerHeartbeat?> GetCurrentAsync(CancellationToken cancellationToken)
        {
            _ = cancellationToken;
            return Task.FromResult(current);
        }

        public Task SaveAsync(WorkerHeartbeat heartbeat, CancellationToken cancellationToken)
        {
            _ = heartbeat;
            _ = cancellationToken;
            throw new NotSupportedException();
        }
    }

    private sealed class FixedTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => utcNow;
    }
}

using Microsoft.Extensions.Logging.Abstractions;
using SyncFactors.Contracts;
using SyncFactors.Domain;

namespace SyncFactors.Domain.Tests;

public sealed class SyncScheduleCoordinatorTests
{
    [Fact]
    public async Task TryEnqueueDueRunAsync_QueuesScheduledRun_WhenDueAndIdle()
    {
        var queueStore = new CapturingRunQueueStore();
        var scheduleStore = new StubSyncScheduleStore(
            new SyncScheduleStatus(
                Enabled: true,
                IntervalMinutes: 30,
                NextRunAt: DateTimeOffset.Parse("2026-03-30T11:30:00Z"),
                LastScheduledRunAt: null,
                LastEnqueueAttemptAt: null,
                LastEnqueueError: null));
        var coordinator = new SyncScheduleCoordinator(
            scheduleStore,
            queueStore,
            new RealSyncSettings(),
            new FakeTimeProvider(DateTimeOffset.Parse("2026-03-30T12:00:00Z")),
            NullLogger<SyncScheduleCoordinator>.Instance);

        var queued = await coordinator.TryEnqueueDueRunAsync(CancellationToken.None);

        Assert.True(queued);
        Assert.NotNull(queueStore.LastRequest);
        Assert.False(queueStore.LastRequest!.DryRun);
        Assert.Equal("Scheduled", queueStore.LastRequest.RunTrigger);
        Assert.Equal("Sync schedule", queueStore.LastRequest.RequestedBy);
        Assert.Equal(DateTimeOffset.Parse("2026-03-30T12:00:00Z"), scheduleStore.LastSuccessfulEnqueueAt);
    }

    [Fact]
    public async Task TryEnqueueDueRunAsync_QueuesDryRun_WhenRealSyncIsDisabled()
    {
        var queueStore = new CapturingRunQueueStore();
        var scheduleStore = new StubSyncScheduleStore(
            new SyncScheduleStatus(
                Enabled: true,
                IntervalMinutes: 30,
                NextRunAt: DateTimeOffset.Parse("2026-03-30T11:30:00Z"),
                LastScheduledRunAt: null,
                LastEnqueueAttemptAt: null,
                LastEnqueueError: null));
        var coordinator = new SyncScheduleCoordinator(
            scheduleStore,
            queueStore,
            new RealSyncSettings(Enabled: false),
            new FakeTimeProvider(DateTimeOffset.Parse("2026-03-30T12:00:00Z")),
            NullLogger<SyncScheduleCoordinator>.Instance);

        var queued = await coordinator.TryEnqueueDueRunAsync(CancellationToken.None);

        Assert.True(queued);
        Assert.NotNull(queueStore.LastRequest);
        Assert.True(queueStore.LastRequest!.DryRun);
    }

    [Fact]
    public async Task TryEnqueueDueRunAsync_DoesNotQueue_WhenAnotherRunIsPending()
    {
        var queueStore = new CapturingRunQueueStore { HasPendingOrActiveRun = true };
        var scheduleStore = new StubSyncScheduleStore(
            new SyncScheduleStatus(
                Enabled: true,
                IntervalMinutes: 30,
                NextRunAt: DateTimeOffset.Parse("2026-03-30T11:30:00Z"),
                LastScheduledRunAt: null,
                LastEnqueueAttemptAt: null,
                LastEnqueueError: null));
        var coordinator = new SyncScheduleCoordinator(
            scheduleStore,
            queueStore,
            new RealSyncSettings(),
            new FakeTimeProvider(DateTimeOffset.Parse("2026-03-30T12:00:00Z")),
            NullLogger<SyncScheduleCoordinator>.Instance);

        var queued = await coordinator.TryEnqueueDueRunAsync(CancellationToken.None);

        Assert.False(queued);
        Assert.Null(queueStore.LastRequest);
        Assert.Null(scheduleStore.LastSuccessfulEnqueueAt);
    }

    private sealed class CapturingRunQueueStore : IRunQueueStore
    {
        public bool HasPendingOrActiveRun { get; set; }

        public StartRunRequest? LastRequest { get; private set; }

        public Task<RunQueueRequest> EnqueueAsync(StartRunRequest request, CancellationToken cancellationToken)
        {
            _ = cancellationToken;
            LastRequest = request;
            return Task.FromResult(new RunQueueRequest("req-1", "BulkSync", request.DryRun, request.RunTrigger, request.RequestedBy, "Pending", DateTimeOffset.UtcNow, null, null, null, null));
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
            return Task.FromResult<RunQueueRequest?>(null);
        }

        public Task<bool> HasPendingOrActiveRunAsync(CancellationToken cancellationToken)
        {
            _ = cancellationToken;
            return Task.FromResult(HasPendingOrActiveRun);
        }

        public Task<bool> CancelPendingOrActiveAsync(string? requestedBy, CancellationToken cancellationToken)
        {
            _ = requestedBy;
            _ = cancellationToken;
            return Task.FromResult(false);
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
            return Task.CompletedTask;
        }

        public Task CancelAsync(string requestId, string? runId, string? errorMessage, CancellationToken cancellationToken)
        {
            _ = requestId;
            _ = runId;
            _ = errorMessage;
            _ = cancellationToken;
            return Task.CompletedTask;
        }

        public Task FailAsync(string requestId, string? runId, string errorMessage, CancellationToken cancellationToken)
        {
            _ = requestId;
            _ = runId;
            _ = errorMessage;
            _ = cancellationToken;
            return Task.CompletedTask;
        }
    }

    private sealed class StubSyncScheduleStore(SyncScheduleStatus current) : ISyncScheduleStore
    {
        public DateTimeOffset? LastSuccessfulEnqueueAt { get; private set; }

        public Task<SyncScheduleStatus> GetCurrentAsync(CancellationToken cancellationToken)
        {
            _ = cancellationToken;
            return Task.FromResult(current);
        }

        public Task<SyncScheduleStatus> UpdateAsync(UpdateSyncScheduleRequest request, CancellationToken cancellationToken)
        {
            _ = request;
            _ = cancellationToken;
            return Task.FromResult(current);
        }

        public Task<SyncScheduleStatus> RecordSuccessfulEnqueueAsync(DateTimeOffset enqueuedAt, CancellationToken cancellationToken)
        {
            _ = cancellationToken;
            LastSuccessfulEnqueueAt = enqueuedAt;
            return Task.FromResult(current);
        }

        public Task<SyncScheduleStatus> RecordFailedEnqueueAsync(DateTimeOffset attemptedAt, string errorMessage, CancellationToken cancellationToken)
        {
            _ = attemptedAt;
            _ = errorMessage;
            _ = cancellationToken;
            return Task.FromResult(current);
        }
    }

    private sealed class FakeTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => utcNow;
    }
}

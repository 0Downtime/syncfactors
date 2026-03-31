using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using SyncFactors.Api.Pages;
using SyncFactors.Contracts;
using SyncFactors.Domain;

namespace SyncFactors.Api.Tests;

public sealed class SyncModelTests
{
    [Fact]
    public async Task OnGetAsync_LoadsScheduleAndRecentRuns()
    {
        var queueStore = new CapturingRunQueueStore();
        var scheduleStore = new StubSyncScheduleStore();
        var model = new SyncModel(CreateDashboardService(), queueStore, scheduleStore);

        await model.OnGetAsync(CancellationToken.None);

        Assert.Equal(30, model.Schedule.IntervalMinutes);
        Assert.Single(model.Runs);
        Assert.False(model.HasPendingOrActiveRun);
    }

    [Fact]
    public async Task OnPostStartRunAsync_QueuesDryRunByDefault()
    {
        var queueStore = new CapturingRunQueueStore();
        var model = new SyncModel(CreateDashboardService(), queueStore, new StubSyncScheduleStore());

        var result = await model.OnPostStartRunAsync(CancellationToken.None);

        Assert.IsType<PageResult>(result);
        Assert.NotNull(queueStore.LastRequest);
        Assert.True(queueStore.LastRequest!.DryRun);
        Assert.Equal("AdHoc", queueStore.LastRequest.RunTrigger);
        Assert.Equal("Sync page", queueStore.LastRequest.RequestedBy);
    }

    [Fact]
    public async Task OnPostStartRunAsync_QueuesLiveRunWhenSelected()
    {
        var queueStore = new CapturingRunQueueStore();
        var model = new SyncModel(CreateDashboardService(), queueStore, new StubSyncScheduleStore())
        {
            RunMode = "LiveRun"
        };

        var result = await model.OnPostStartRunAsync(CancellationToken.None);

        Assert.IsType<PageResult>(result);
        Assert.NotNull(queueStore.LastRequest);
        Assert.False(queueStore.LastRequest!.DryRun);
    }

    [Fact]
    public async Task OnPostSaveScheduleAsync_UpdatesSchedule()
    {
        var scheduleStore = new StubSyncScheduleStore();
        var model = new SyncModel(CreateDashboardService(), new CapturingRunQueueStore(), scheduleStore)
        {
            ScheduleEnabled = true,
            IntervalMinutes = 45
        };

        var result = await model.OnPostSaveScheduleAsync(CancellationToken.None);

        Assert.IsType<PageResult>(result);
        Assert.NotNull(scheduleStore.LastUpdateRequest);
        Assert.True(scheduleStore.LastUpdateRequest!.Enabled);
        Assert.Equal(45, scheduleStore.LastUpdateRequest.IntervalMinutes);
        Assert.True(model.Schedule.Enabled);
        Assert.Equal(45, model.Schedule.IntervalMinutes);
    }

    private static IDashboardSnapshotService CreateDashboardService()
    {
        return new StubDashboardSnapshotService(
            new DashboardSnapshot(
                Status: new RuntimeStatus("Idle", "NotStarted", null, null, true, 0, 0, null, null, null, null, null, null),
                Runs:
                [
                    new RunSummary(
                        RunId: "bulk-1",
                        Path: null,
                        ArtifactType: "BulkRun",
                        ConfigPath: null,
                        MappingConfigPath: null,
                        Mode: "BulkSync",
                        DryRun: true,
                        Status: "Succeeded",
                        StartedAt: DateTimeOffset.Parse("2026-03-30T12:00:00Z"),
                        CompletedAt: DateTimeOffset.Parse("2026-03-30T12:05:00Z"),
                        DurationSeconds: 300,
                        ProcessedWorkers: 10,
                        TotalWorkers: 10,
                        Creates: 2,
                        Updates: 8,
                        Enables: 0,
                        Disables: 0,
                        GraveyardMoves: 0,
                        Deletions: 0,
                        Quarantined: 0,
                        Conflicts: 0,
                        GuardrailFailures: 0,
                        ManualReview: 0,
                        Unchanged: 0,
                        RunTrigger: "AdHoc",
                        RequestedBy: "Sync page")
                ],
                ActiveRun: null,
                LastCompletedRun: null,
                RequiresAttention: false,
                AttentionMessage: null,
                CheckedAt: DateTimeOffset.Parse("2026-03-30T12:06:00Z")));
    }

    private sealed class StubDashboardSnapshotService(DashboardSnapshot snapshot) : IDashboardSnapshotService
    {
        public Task<DashboardSnapshot> GetSnapshotAsync(CancellationToken cancellationToken)
        {
            _ = cancellationToken;
            return Task.FromResult(snapshot);
        }
    }

    private sealed class CapturingRunQueueStore : IRunQueueStore
    {
        public StartRunRequest? LastRequest { get; private set; }

        public bool HasPendingOrActiveRun { get; set; }

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

        public Task<bool> HasPendingOrActiveRunAsync(CancellationToken cancellationToken)
        {
            _ = cancellationToken;
            return Task.FromResult(HasPendingOrActiveRun);
        }

        public Task CompleteAsync(string requestId, string runId, CancellationToken cancellationToken)
        {
            _ = requestId;
            _ = runId;
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

    private sealed class StubSyncScheduleStore : ISyncScheduleStore
    {
        public UpdateSyncScheduleRequest? LastUpdateRequest { get; private set; }

        private SyncScheduleStatus _current = new(false, 30, null, null, null, null);

        public Task<SyncScheduleStatus> GetCurrentAsync(CancellationToken cancellationToken)
        {
            _ = cancellationToken;
            return Task.FromResult(_current);
        }

        public Task<SyncScheduleStatus> UpdateAsync(UpdateSyncScheduleRequest request, CancellationToken cancellationToken)
        {
            _ = cancellationToken;
            LastUpdateRequest = request;
            _current = new SyncScheduleStatus(request.Enabled, request.IntervalMinutes, request.Enabled ? DateTimeOffset.Parse("2026-03-30T12:30:00Z") : null, null, null, null);
            return Task.FromResult(_current);
        }

        public Task<SyncScheduleStatus> RecordSuccessfulEnqueueAsync(DateTimeOffset enqueuedAt, CancellationToken cancellationToken)
        {
            _ = enqueuedAt;
            _ = cancellationToken;
            return Task.FromResult(_current);
        }

        public Task<SyncScheduleStatus> RecordFailedEnqueueAsync(DateTimeOffset attemptedAt, string errorMessage, CancellationToken cancellationToken)
        {
            _ = attemptedAt;
            _ = errorMessage;
            _ = cancellationToken;
            return Task.FromResult(_current);
        }
    }
}

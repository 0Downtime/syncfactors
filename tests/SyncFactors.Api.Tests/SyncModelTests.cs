using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.Http;
using System.Security.Claims;
using SyncFactors.Api.Pages;
using SyncFactors.Contracts;
using SyncFactors.Domain;
using SyncFactors.Infrastructure;

namespace SyncFactors.Api.Tests;

public sealed class SyncModelTests
{
    [Fact]
    public async Task OnGetAsync_LoadsScheduleAndRecentRuns()
    {
        var queueStore = new CapturingRunQueueStore();
        var scheduleStore = new StubSyncScheduleStore();
        var model = new SyncModel(CreateDashboardService(), queueStore, new RealSyncSettings(), scheduleStore);

        await model.OnGetAsync(CancellationToken.None);

        Assert.Equal(30, model.Schedule.IntervalMinutes);
        Assert.Single(model.Runs);
        Assert.False(model.HasPendingOrActiveRun);
    }

    [Fact]
    public async Task OnPostStartRunAsync_QueuesDryRunByDefault()
    {
        var queueStore = new CapturingRunQueueStore();
        var model = new SyncModel(CreateDashboardService(), queueStore, new RealSyncSettings(), new StubSyncScheduleStore());

        var result = await model.OnPostStartRunAsync(CancellationToken.None);

        var redirect = Assert.IsType<RedirectToPageResult>(result);
        Assert.Equal("/Index", redirect.PageName);
        Assert.NotNull(queueStore.LastRequest);
        Assert.True(queueStore.LastRequest!.DryRun);
        Assert.Equal("BulkSync", queueStore.LastRequest.Mode);
        Assert.Equal("AdHoc", queueStore.LastRequest.RunTrigger);
        Assert.Equal("Sync page", queueStore.LastRequest.RequestedBy);
        Assert.Equal("Dry-run sync queued.", model.SuccessMessage);
        Assert.Null(model.ErrorMessage);
    }

    [Fact]
    public async Task OnPostStartRunAsync_QueuesLiveRunWhenSelected()
    {
        var queueStore = new CapturingRunQueueStore();
        var model = new SyncModel(CreateDashboardService(), queueStore, new RealSyncSettings(), new StubSyncScheduleStore())
        {
            RunMode = "LiveRun"
        };

        var result = await model.OnPostStartRunAsync(CancellationToken.None);

        var redirect = Assert.IsType<RedirectToPageResult>(result);
        Assert.Equal("/Index", redirect.PageName);
        Assert.NotNull(queueStore.LastRequest);
        Assert.False(queueStore.LastRequest!.DryRun);
        Assert.Equal("BulkSync", queueStore.LastRequest.Mode);
        Assert.Equal("Live provisioning run queued.", model.SuccessMessage);
        Assert.Null(model.ErrorMessage);
    }

    [Fact]
    public async Task OnPostStartRunAsync_RejectsLiveRunWhenRealSyncIsDisabled()
    {
        var queueStore = new CapturingRunQueueStore();
        var model = new SyncModel(CreateDashboardService(), queueStore, new RealSyncSettings(Enabled: false), new StubSyncScheduleStore())
        {
            RunMode = "LiveRun"
        };

        var result = await model.OnPostStartRunAsync(CancellationToken.None);

        Assert.IsType<RedirectToPageResult>(result);
        Assert.Null(queueStore.LastRequest);
        Assert.Equal("Live provisioning is disabled for this environment. Queue a dry run instead.", model.ErrorMessage);
        Assert.Null(model.SuccessMessage);
    }

    [Fact]
    public async Task OnPostStartRunAsync_UsesAuthenticatedUsernameWhenAvailable()
    {
        var queueStore = new CapturingRunQueueStore();
        var model = new SyncModel(CreateDashboardService(), queueStore, new RealSyncSettings(), new StubSyncScheduleStore());
        AttachAuthenticatedUser(model, "operator@example.com");

        await model.OnPostStartRunAsync(CancellationToken.None);

        Assert.Equal("operator@example.com", queueStore.LastRequest?.RequestedBy);
    }

    [Fact]
    public async Task OnPostDeleteAllUsersAsync_QueuesDeleteAllUsersRunWhenPhraseMatches()
    {
        var queueStore = new CapturingRunQueueStore();
        var model = new SyncModel(CreateDashboardService(), queueStore, new RealSyncSettings(), new StubSyncScheduleStore())
        {
            DeleteAllUsersConfirmationText = SyncModel.DeleteAllUsersConfirmationPhrase
        };

        var result = await model.OnPostDeleteAllUsersAsync(CancellationToken.None);

        Assert.IsType<RedirectToPageResult>(result);
        Assert.NotNull(queueStore.LastRequest);
        Assert.False(queueStore.LastRequest!.DryRun);
        Assert.Equal("DeleteAllUsers", queueStore.LastRequest.Mode);
        Assert.Equal("DeleteAllUsers", queueStore.LastRequest.RunTrigger);
        Assert.Equal("Delete-all AD reset queued.", model.SuccessMessage);
        Assert.Null(model.ErrorMessage);
    }

    [Fact]
    public async Task OnPostDeleteAllUsersAsync_RejectsQueueWhenRealSyncIsDisabled()
    {
        var queueStore = new CapturingRunQueueStore();
        var model = new SyncModel(CreateDashboardService(), queueStore, new RealSyncSettings(Enabled: false), new StubSyncScheduleStore())
        {
            DeleteAllUsersConfirmationText = SyncModel.DeleteAllUsersConfirmationPhrase
        };

        var result = await model.OnPostDeleteAllUsersAsync(CancellationToken.None);

        Assert.IsType<RedirectToPageResult>(result);
        Assert.Null(queueStore.LastRequest);
        Assert.Equal("Real AD sync is disabled for this environment.", model.ErrorMessage);
        Assert.Null(model.SuccessMessage);
    }

    [Fact]
    public async Task OnPostDeleteAllUsersAsync_RejectsInvalidConfirmationPhrase()
    {
        var queueStore = new CapturingRunQueueStore();
        var model = new SyncModel(CreateDashboardService(), queueStore, new RealSyncSettings(), new StubSyncScheduleStore())
        {
            DeleteAllUsersConfirmationText = "delete all users"
        };

        var result = await model.OnPostDeleteAllUsersAsync(CancellationToken.None);

        Assert.IsType<RedirectToPageResult>(result);
        Assert.Null(queueStore.LastRequest);
        Assert.Equal($"Type {SyncModel.DeleteAllUsersConfirmationPhrase} to queue the delete-all AD reset run.", model.ErrorMessage);
        Assert.Null(model.SuccessMessage);
    }

    [Fact]
    public async Task OnPostStartRunAsync_WhenRunAlreadyPending_RedirectsWithError()
    {
        var queueStore = new CapturingRunQueueStore
        {
            HasPendingOrActiveRun = true
        };
        var model = new SyncModel(CreateDashboardService(), queueStore, new RealSyncSettings(), new StubSyncScheduleStore());

        var result = await model.OnPostStartRunAsync(CancellationToken.None);

        Assert.IsType<RedirectToPageResult>(result);
        Assert.Null(queueStore.LastRequest);
        Assert.Equal("A run is already pending or in progress.", model.ErrorMessage);
        Assert.Null(model.SuccessMessage);
    }

    [Fact]
    public async Task OnPostCancelRunAsync_RequestsCancellation()
    {
        var queueStore = new CapturingRunQueueStore
        {
            PendingOrActiveRun = new RunQueueRequest("req-1", "BulkSync", true, "AdHoc", "Sync page", "InProgress", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, null, "bulk-1", null)
        };
        var model = new SyncModel(CreateDashboardService(), queueStore, new RealSyncSettings(), new StubSyncScheduleStore());

        var result = await model.OnPostCancelRunAsync(CancellationToken.None);

        Assert.IsType<RedirectToPageResult>(result);
        Assert.True(queueStore.CancelRequested);
        Assert.Equal("Run cancellation requested.", model.SuccessMessage);
        Assert.Null(model.ErrorMessage);
    }

    [Fact]
    public async Task OnPostSaveScheduleAsync_UpdatesSchedule()
    {
        var scheduleStore = new StubSyncScheduleStore();
        var model = new SyncModel(CreateDashboardService(), new CapturingRunQueueStore(), new RealSyncSettings(), scheduleStore)
        {
            ScheduleEnabled = true,
            IntervalMinutes = 45
        };
        AttachAuthenticatedUser(model, "admin@example.com", SecurityRoles.Admin);

        var result = await model.OnPostSaveScheduleAsync(CancellationToken.None);

        Assert.IsType<PageResult>(result);
        Assert.NotNull(scheduleStore.LastUpdateRequest);
        Assert.True(scheduleStore.LastUpdateRequest!.Enabled);
        Assert.Equal(45, scheduleStore.LastUpdateRequest.IntervalMinutes);
        Assert.True(model.Schedule.Enabled);
        Assert.Equal(45, model.Schedule.IntervalMinutes);
    }

    [Fact]
    public async Task OnPostSaveScheduleAsync_UsesDryRunLabelWhenRealSyncIsDisabled()
    {
        var scheduleStore = new StubSyncScheduleStore();
        var model = new SyncModel(CreateDashboardService(), new CapturingRunQueueStore(), new RealSyncSettings(Enabled: false), scheduleStore)
        {
            ScheduleEnabled = true,
            IntervalMinutes = 45
        };
        AttachAuthenticatedUser(model, "admin@example.com", SecurityRoles.Admin);

        await model.OnPostSaveScheduleAsync(CancellationToken.None);

        Assert.Equal("Recurring dry-run sync enabled every 45 minutes.", model.SuccessMessage);
    }

    [Fact]
    public async Task OnPostSaveScheduleAsync_ForbidsOperators()
    {
        var scheduleStore = new StubSyncScheduleStore();
        var model = new SyncModel(CreateDashboardService(), new CapturingRunQueueStore(), new RealSyncSettings(), scheduleStore)
        {
            ScheduleEnabled = true,
            IntervalMinutes = 45
        };
        AttachAuthenticatedUser(model, "operator@example.com", SecurityRoles.Operator);

        var result = await model.OnPostSaveScheduleAsync(CancellationToken.None);

        Assert.IsType<ForbidResult>(result);
        Assert.Null(scheduleStore.LastUpdateRequest);
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
                        SyncScope: "Delta",
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

    private static void AttachAuthenticatedUser(PageModel model, string username, string role = SecurityRoles.Operator)
    {
        model.PageContext = new PageContext
        {
            HttpContext = new DefaultHttpContext
            {
                User = new ClaimsPrincipal(
                    new ClaimsIdentity(
                    [
                        new Claim(ClaimTypes.Name, username),
                        new Claim(ClaimTypes.Role, role)
                    ],
                    "Cookies"))
            }
        };
    }

    private sealed class CapturingRunQueueStore : IRunQueueStore
    {
        public StartRunRequest? LastRequest { get; private set; }

        public bool HasPendingOrActiveRun { get; set; }

        public bool CancelRequested { get; private set; }

        public RunQueueRequest? PendingOrActiveRun { get; set; }

        public Task<RunQueueRequest> EnqueueAsync(StartRunRequest request, CancellationToken cancellationToken)
        {
            _ = cancellationToken;
            LastRequest = request;
            return Task.FromResult(new RunQueueRequest("req-1", request.Mode, request.DryRun, request.RunTrigger, request.RequestedBy, "Pending", DateTimeOffset.UtcNow, null, null, null, null));
        }

        public Task<RunQueueRequest?> ClaimNextPendingAsync(string workerName, CancellationToken cancellationToken)
        {
            _ = workerName;
            _ = cancellationToken;
            return Task.FromResult<RunQueueRequest?>(null);
        }

        public Task<RunQueueRequest?> GetAsync(string requestId, CancellationToken cancellationToken)
        {
            _ = requestId;
            _ = cancellationToken;
            return Task.FromResult(PendingOrActiveRun);
        }

        public Task<RunQueueRequest?> GetPendingOrActiveAsync(CancellationToken cancellationToken)
        {
            _ = cancellationToken;
            return Task.FromResult(PendingOrActiveRun);
        }

        public Task<bool> HasPendingOrActiveRunAsync(CancellationToken cancellationToken)
        {
            _ = cancellationToken;
            return Task.FromResult(HasPendingOrActiveRun || PendingOrActiveRun is not null);
        }

        public Task<bool> CancelPendingOrActiveAsync(string? requestedBy, CancellationToken cancellationToken)
        {
            _ = requestedBy;
            _ = cancellationToken;
            CancelRequested = PendingOrActiveRun is not null || HasPendingOrActiveRun;
            return Task.FromResult(CancelRequested);
        }

        public Task<bool> IsCancellationRequestedAsync(string requestId, CancellationToken cancellationToken)
        {
            _ = requestId;
            _ = cancellationToken;
            return Task.FromResult(CancelRequested);
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
            CancelRequested = true;
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

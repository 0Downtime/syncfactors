using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.Logging.Abstractions;
using SyncFactors.Api.Pages.Admin;
using SyncFactors.Contracts;
using SyncFactors.Domain;

namespace SyncFactors.Api.Tests;

public sealed class AdminDeletionQueueModelTests
{
    [Fact]
    public async Task OnGetAsync_LoadsPendingAndHeldUsers()
    {
        var model = CreateModel(new CapturingRetentionStore(
            [
                CreateRecord("10001", false),
                CreateRecord("10002", true)
            ]));

        await model.OnGetAsync(CancellationToken.None);

        Assert.Single(model.PendingUsers);
        Assert.Single(model.HeldUsers);
        Assert.Equal("10001", model.PendingUsers[0].WorkerId);
        Assert.Equal("10002", model.HeldUsers[0].WorkerId);
    }

    [Fact]
    public async Task OnGetAsync_FiltersPendingAndHeldUsers_BySearchText()
    {
        var model = CreateModel(new CapturingRetentionStore(
            [
                CreateRecord("10001", false),
                CreateRecord("20002", false),
                CreateRecord("30003", true)
            ]));
        model.Filter = "30003";

        await model.OnGetAsync(CancellationToken.None);

        Assert.Empty(model.PendingUsers);
        Assert.Single(model.HeldUsers);
        Assert.Equal("30003", model.HeldUsers[0].WorkerId);
        Assert.Equal(0, model.TotalPendingUsers);
        Assert.Equal(1, model.TotalHeldUsers);
    }

    [Fact]
    public async Task OnGetAsync_PaginatesPendingUsers_AtTwentyFivePerPage()
    {
        var records = Enumerable.Range(1, 30)
            .Select(index => CreateRecord(index.ToString("D5"), false))
            .ToArray();
        var model = CreateModel(new CapturingRetentionStore(records));
        model.PendingPageNumber = 2;

        await model.OnGetAsync(CancellationToken.None);

        Assert.Equal(30, model.TotalPendingUsers);
        Assert.Equal(2, model.TotalPendingPages);
        Assert.Equal(5, model.PendingUsers.Count);
        Assert.Equal("00026", model.PendingUsers[0].WorkerId);
        Assert.True(model.HasPreviousPendingPage);
        Assert.False(model.HasNextPendingPage);
    }

    [Fact]
    public async Task OnGetAsync_PaginatesHeldUsers_AtTwentyFivePerPage()
    {
        var records = Enumerable.Range(1, 30)
            .Select(index => CreateRecord(index.ToString("D5"), true))
            .ToArray();
        var model = CreateModel(new CapturingRetentionStore(records));
        model.HeldPageNumber = 2;

        await model.OnGetAsync(CancellationToken.None);

        Assert.Equal(30, model.TotalHeldUsers);
        Assert.Equal(2, model.TotalHeldPages);
        Assert.Equal(5, model.HeldUsers.Count);
        Assert.Equal("00026", model.HeldUsers[0].WorkerId);
        Assert.True(model.HasPreviousHeldPage);
        Assert.False(model.HasNextHeldPage);
    }

    [Fact]
    public async Task OnPostPlaceHoldAsync_UsesCurrentUserAsActor()
    {
        var store = new CapturingRetentionStore([CreateRecord("10001", false)]);
        var model = CreateModel(store, actingUserId: "admin-1");

        var result = await model.OnPostPlaceHoldAsync("10001", CancellationToken.None);

        Assert.IsType<RedirectToPageResult>(result);
        Assert.Equal("10001", store.LastWorkerId);
        Assert.True(store.LastIsOnHold);
        Assert.Equal("admin", store.LastActingUserId);
        Assert.Equal("Placed a deletion hold for worker 10001.", model.SuccessMessage);
    }

    [Fact]
    public async Task OnPostPlaceHoldAsync_PrefersUsernameOverOpaqueNameIdentifier()
    {
        var store = new CapturingRetentionStore([CreateRecord("10001", false)]);
        var model = CreateModel(store, actingUserId: "local-opaque-id", username: "codexadmin");

        await model.OnPostPlaceHoldAsync("10001", CancellationToken.None);

        Assert.Equal("codexadmin", store.LastActingUserId);
    }

    [Fact]
    public async Task OnPostRemoveHoldAsync_ClearsHold()
    {
        var store = new CapturingRetentionStore([CreateRecord("10001", true)]);
        var model = CreateModel(store, actingUserId: "admin-1");

        var result = await model.OnPostRemoveHoldAsync("10001", CancellationToken.None);

        Assert.IsType<RedirectToPageResult>(result);
        Assert.Equal("10001", store.LastWorkerId);
        Assert.False(store.LastIsOnHold);
        Assert.Equal("Removed the deletion hold for worker 10001.", model.SuccessMessage);
    }

    [Fact]
    public async Task OnPostApproveDeleteAsync_DeletesEligibleUser()
    {
        var store = new CapturingRetentionStore([CreateRecord("10001", false)]);
        var commandGateway = new CapturingDirectoryCommandGateway();
        var lifecycle = new CapturingRunLifecycleService();
        var model = CreateModel(store, commandGateway: commandGateway, lifecycle: lifecycle);

        var result = await model.OnPostApproveDeleteAsync("10001", CancellationToken.None);

        Assert.IsType<RedirectToPageResult>(result);
        var command = Assert.Single(commandGateway.Commands);
        Assert.Equal("DeleteUser", command.Action);
        Assert.Equal("10001", command.WorkerId);
        Assert.Equal(["10001"], store.ResolvedWorkerIds);
        Assert.Contains("Deleted", model.SuccessMessage, StringComparison.OrdinalIgnoreCase);
        Assert.Single(lifecycle.Entries);
        Assert.Equal("GraveyardDeleteApproval", lifecycle.CompletedMode);
    }

    private static DeletionQueueModel CreateModel(
        CapturingRetentionStore store,
        string actingUserId = "admin-1",
        string username = "admin",
        CapturingDirectoryCommandGateway? commandGateway = null,
        CapturingRunLifecycleService? lifecycle = null)
    {
        var workerIds = recordsFromStore(store)
            .Select(record => record.WorkerId)
            .ToArray();
        var now = DateTimeOffset.Parse("2026-04-11T12:00:00Z");
        var settings = new GraveyardDeletionQueueSettings(RetentionDays: 30, AutoDeleteEnabled: true);
        var lifecycleSettings = new LifecyclePolicySettings(
            ActiveOu: "OU=Employees,DC=example,DC=com",
            PrehireOu: "OU=Prehire,DC=example,DC=com",
            GraveyardOu: "OU=Graveyard,DC=example,DC=com",
            InactiveStatusField: "emplStatus",
            InactiveStatusValues: ["T"],
            DirectoryIdentityAttribute: "employeeID");
        var directoryGateway = new StubDirectoryGateway(workerIds);
        var service = new GraveyardDeletionQueueService(
            store,
            directoryGateway,
            settings,
            lifecycleSettings,
            new FakeTimeProvider(now));
        var deleteCoordinator = new GraveyardAutoDeleteCoordinator(
            service,
            store,
            commandGateway ?? new CapturingDirectoryCommandGateway(),
            lifecycle ?? new CapturingRunLifecycleService(),
            settings,
            new WorkerRunSettings(MaxCreatesPerRun: 10, MaxDisablesPerRun: 10, MaxDeletionsPerRun: 10),
            NullLogger<GraveyardAutoDeleteCoordinator>.Instance,
            new FakeTimeProvider(now));

        return new DeletionQueueModel(
            service,
            deleteCoordinator,
            store,
            new FakeTimeProvider(now))
        {
            PageContext = new PageContext
            {
                HttpContext = new DefaultHttpContext
                {
                    User = new ClaimsPrincipal(new ClaimsIdentity(
                    [
                        new Claim(ClaimTypes.NameIdentifier, actingUserId),
                        new Claim(ClaimTypes.Name, username),
                        new Claim(ClaimTypes.Role, "Admin")
                    ], "Cookies"))
                }
            }
        };
    }

    private static IReadOnlyList<GraveyardRetentionRecord> recordsFromStore(CapturingRetentionStore store) =>
        store.Records;

    private static GraveyardRetentionRecord CreateRecord(string workerId, bool isOnHold) =>
        new(
            WorkerId: workerId,
            SamAccountName: workerId,
            DisplayName: $"Worker {workerId}",
            DistinguishedName: $"CN=Worker {workerId},OU=Graveyard,DC=example,DC=com",
            Status: "T",
            EndDateUtc: DateTimeOffset.Parse("2026-02-01T00:00:00Z"),
            LastObservedAtUtc: DateTimeOffset.Parse("2026-04-01T00:00:00Z"),
            Active: true,
            IsOnHold: isOnHold,
            HoldPlacedAtUtc: isOnHold ? DateTimeOffset.Parse("2026-04-05T00:00:00Z") : null,
            HoldPlacedBy: isOnHold ? "admin-1" : null);

    private sealed class CapturingRetentionStore(IReadOnlyList<GraveyardRetentionRecord> records) : IGraveyardRetentionStore
    {
        public IReadOnlyList<GraveyardRetentionRecord> Records => records;

        public List<string> ResolvedWorkerIds { get; } = [];

        public string? LastWorkerId { get; private set; }

        public bool LastIsOnHold { get; private set; }

        public string? LastActingUserId { get; private set; }

        public Task UpsertObservedAsync(GraveyardRetentionRecord record, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task ResolveAsync(string workerId, CancellationToken cancellationToken)
        {
            ResolvedWorkerIds.Add(workerId);
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<GraveyardRetentionRecord>> ListActiveAsync(CancellationToken cancellationToken) =>
            Task.FromResult(records);

        public Task SetHoldAsync(string workerId, bool isOnHold, string? actingUserId, DateTimeOffset changedAtUtc, CancellationToken cancellationToken)
        {
            LastWorkerId = workerId;
            LastIsOnHold = isOnHold;
            LastActingUserId = actingUserId;
            return Task.CompletedTask;
        }

        public Task<GraveyardRetentionReportStatus> GetReportStatusAsync(CancellationToken cancellationToken) =>
            Task.FromResult(new GraveyardRetentionReportStatus(null, null, null));

        public Task RecordReportAttemptAsync(DateTimeOffset attemptedAt, string? error, DateTimeOffset? sentAtUtc, CancellationToken cancellationToken) =>
            Task.CompletedTask;
    }

    private sealed class StubDirectoryGateway(params string[] workerIds) : IDirectoryGateway
    {
        private readonly IReadOnlyList<DirectoryUserSnapshot> _users = workerIds
            .Select(workerId => new DirectoryUserSnapshot(
                SamAccountName: workerId,
                DistinguishedName: $"CN=Worker {workerId},OU=Graveyard,DC=example,DC=com",
                Enabled: false,
                DisplayName: $"Worker {workerId}",
                Attributes: new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
                {
                    ["employeeID"] = workerId
                }))
            .ToArray();

        public Task<DirectoryUserSnapshot?> FindByWorkerAsync(WorkerSnapshot worker, CancellationToken cancellationToken) =>
            Task.FromResult<DirectoryUserSnapshot?>(null);

        public Task<IReadOnlyList<DirectoryUserSnapshot>> ListUsersInOuAsync(string ouDistinguishedName, CancellationToken cancellationToken) =>
            Task.FromResult(_users);

        public Task<string?> ResolveManagerDistinguishedNameAsync(string managerId, CancellationToken cancellationToken) =>
            Task.FromResult<string?>(null);

        public Task<string> ResolveAvailableEmailLocalPartAsync(WorkerSnapshot worker, bool isCreate, CancellationToken cancellationToken) =>
            Task.FromResult(worker.WorkerId.ToLowerInvariant());
    }

    private sealed class FakeTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed class CapturingDirectoryCommandGateway : IDirectoryCommandGateway
    {
        public List<DirectoryMutationCommand> Commands { get; } = [];

        public Task<DirectoryCommandResult> ExecuteAsync(DirectoryMutationCommand command, CancellationToken cancellationToken)
        {
            Commands.Add(command);
            return Task.FromResult(new DirectoryCommandResult(true, command.Action, command.SamAccountName, command.CurrentDistinguishedName, "Deleted", null));
        }
    }

    private sealed class CapturingRunLifecycleService : IRunLifecycleService
    {
        public string? CompletedMode { get; private set; }

        public List<RunEntryRecord> Entries { get; } = [];

        public Task ExecutePlannedRunAsync(RunPlan plan, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task StartRunAsync(string runId, string mode, bool dryRun, string runTrigger, string? requestedBy, int totalWorkers, string? initialAction, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task RecordProgressAsync(string runId, string mode, bool dryRun, int processedWorkers, int totalWorkers, string? currentWorkerId, string? lastAction, RunTally tally, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task AppendRunEntryAsync(string runId, RunEntryRecord entry, CancellationToken cancellationToken)
        {
            Entries.Add(entry);
            return Task.CompletedTask;
        }

        public Task CompleteRunAsync(string runId, string mode, bool dryRun, int totalWorkers, RunTally tally, System.Text.Json.JsonElement report, DateTimeOffset startedAt, CancellationToken cancellationToken)
        {
            CompletedMode = mode;
            return Task.CompletedTask;
        }

        public Task CancelRunAsync(string runId, string mode, bool dryRun, int processedWorkers, int totalWorkers, string? currentWorkerId, string? reason, RunTally tally, System.Text.Json.JsonElement report, DateTimeOffset startedAt, CancellationToken cancellationToken) =>
            Task.CompletedTask;

        public Task FailRunAsync(string runId, string mode, bool dryRun, int processedWorkers, int totalWorkers, string? currentWorkerId, string errorMessage, RunTally tally, System.Text.Json.JsonElement report, DateTimeOffset startedAt, CancellationToken cancellationToken) =>
            Task.CompletedTask;
    }
}

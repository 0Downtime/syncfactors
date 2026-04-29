using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using SyncFactors.Contracts;
using SyncFactors.Domain;

namespace SyncFactors.Domain.Tests;

public sealed class GraveyardAutoDeleteCoordinatorTests
{
    [Fact]
    public async Task TryExecuteAsync_ReturnsNull_WhenAutoDeleteIsDisabled()
    {
        var retentionStore = new StubGraveyardRetentionStore(
            [
                CreateRecord("10001", isOnHold: false, endDateUtc: DateTimeOffset.Parse("2026-02-01T00:00:00Z"))
            ]);
        var coordinator = CreateCoordinator(
            retentionStore,
            CreateDirectoryGateway("10001"),
            new CapturingDirectoryCommandGateway(),
            new CapturingRunLifecycleService(),
            autoDeleteEnabled: false,
            now: DateTimeOffset.Parse("2026-04-11T12:00:00Z"));

        var runId = await coordinator.TryExecuteAsync(CancellationToken.None);

        Assert.Null(runId);
    }

    [Fact]
    public async Task TryExecuteAsync_DeletesEligibleUsers_AndSkipsHeldUsers()
    {
        var retentionStore = new StubGraveyardRetentionStore(
            [
                CreateRecord("10001", isOnHold: false, endDateUtc: DateTimeOffset.Parse("2026-02-01T00:00:00Z")),
                CreateRecord("10002", isOnHold: true, endDateUtc: DateTimeOffset.Parse("2026-02-01T00:00:00Z"))
            ]);
        var commandGateway = new CapturingDirectoryCommandGateway();
        var lifecycle = new CapturingRunLifecycleService();
        var coordinator = CreateCoordinator(
            retentionStore,
            CreateDirectoryGateway("10001", "10002"),
            commandGateway,
            lifecycle,
            autoDeleteEnabled: true,
            now: DateTimeOffset.Parse("2026-04-11T12:00:00Z"));

        var runId = await coordinator.TryExecuteAsync(CancellationToken.None);

        Assert.NotNull(runId);
        Assert.Single(commandGateway.Commands);
        Assert.Equal("10001", commandGateway.Commands[0].WorkerId);
        Assert.Equal(["10001"], retentionStore.ResolvedWorkerIds);
        Assert.Equal(1, lifecycle.CompletedCalls);
        Assert.Contains(lifecycle.Entries, entry => entry.WorkerId == "10001" && entry.Bucket == "deletions");
        Assert.DoesNotContain(lifecycle.Entries, entry => entry.WorkerId == "10002");
    }

    [Fact]
    public async Task TryExecuteAsync_HonorsDeletionGuardrail()
    {
        var retentionStore = new StubGraveyardRetentionStore(
            [
                CreateRecord("10001", isOnHold: false, endDateUtc: DateTimeOffset.Parse("2026-02-01T00:00:00Z")),
                CreateRecord("10002", isOnHold: false, endDateUtc: DateTimeOffset.Parse("2026-02-02T00:00:00Z"))
            ]);
        var lifecycle = new CapturingRunLifecycleService();
        var coordinator = CreateCoordinator(
            retentionStore,
            CreateDirectoryGateway("10001", "10002"),
            new CapturingDirectoryCommandGateway(),
            lifecycle,
            autoDeleteEnabled: true,
            now: DateTimeOffset.Parse("2026-04-11T12:00:00Z"),
            maxDeletionsPerRun: 1);

        var ex = await Assert.ThrowsAsync<GuardrailExceededException>(() => coordinator.TryExecuteAsync(CancellationToken.None));

        Assert.Contains("Deletion guardrail exceeded", ex.Message, StringComparison.Ordinal);
        Assert.Equal(1, lifecycle.FailedCalls);
        Assert.Contains(lifecycle.Entries, entry => entry.WorkerId == "10002" && entry.Bucket == "guardrailFailures");
    }

    [Fact]
    public async Task ApproveDeleteAsync_DeletesEligibleUser_WhenAutoDeleteIsDisabled()
    {
        var retentionStore = new StubGraveyardRetentionStore(
            [
                CreateRecord("10001", isOnHold: false, endDateUtc: DateTimeOffset.Parse("2026-02-01T00:00:00Z"))
            ]);
        var commandGateway = new CapturingDirectoryCommandGateway();
        var lifecycle = new CapturingRunLifecycleService();
        var coordinator = CreateCoordinator(
            retentionStore,
            CreateDirectoryGateway("10001"),
            commandGateway,
            lifecycle,
            autoDeleteEnabled: false,
            now: DateTimeOffset.Parse("2026-04-11T12:00:00Z"));

        var result = await coordinator.ApproveDeleteAsync("10001", "admin", CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.StartsWith("graveyard-delete-approval-", result.RunId, StringComparison.Ordinal);
        Assert.Single(commandGateway.Commands);
        Assert.Equal(["10001"], retentionStore.ResolvedWorkerIds);
        Assert.Equal(1, lifecycle.CompletedCalls);
        Assert.Contains(lifecycle.Entries, entry => entry.WorkerId == "10001" && entry.Bucket == "deletions");
    }

    [Fact]
    public async Task ApproveDeleteAsync_BlocksHeldUser()
    {
        var retentionStore = new StubGraveyardRetentionStore(
            [
                CreateRecord("10001", isOnHold: true, endDateUtc: DateTimeOffset.Parse("2026-02-01T00:00:00Z"))
            ]);
        var commandGateway = new CapturingDirectoryCommandGateway();
        var lifecycle = new CapturingRunLifecycleService();
        var coordinator = CreateCoordinator(
            retentionStore,
            CreateDirectoryGateway("10001"),
            commandGateway,
            lifecycle,
            autoDeleteEnabled: false,
            now: DateTimeOffset.Parse("2026-04-11T12:00:00Z"));

        var result = await coordinator.ApproveDeleteAsync("10001", "admin", CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Contains("on hold", result.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(commandGateway.Commands);
        Assert.Empty(lifecycle.Entries);
    }

    private static GraveyardAutoDeleteCoordinator CreateCoordinator(
        StubGraveyardRetentionStore retentionStore,
        IDirectoryGateway directoryGateway,
        IDirectoryCommandGateway commandGateway,
        CapturingRunLifecycleService lifecycle,
        bool autoDeleteEnabled,
        DateTimeOffset now,
        int maxDeletionsPerRun = 10)
    {
        var queueService = new GraveyardDeletionQueueService(
            retentionStore,
            directoryGateway,
            new GraveyardDeletionQueueSettings(RetentionDays: 30, AutoDeleteEnabled: autoDeleteEnabled),
            CreateLifecycleSettings(),
            new FakeTimeProvider(now));

        return new GraveyardAutoDeleteCoordinator(
            queueService,
            retentionStore,
            commandGateway,
            lifecycle,
            new GraveyardDeletionQueueSettings(RetentionDays: 30, AutoDeleteEnabled: autoDeleteEnabled),
            new WorkerRunSettings(MaxCreatesPerRun: 10, MaxDisablesPerRun: 10, MaxDeletionsPerRun: maxDeletionsPerRun),
            NullLogger<GraveyardAutoDeleteCoordinator>.Instance,
            new FakeTimeProvider(now));
    }

    private static GraveyardRetentionRecord CreateRecord(string workerId, bool isOnHold, DateTimeOffset endDateUtc) =>
        new(
            WorkerId: workerId,
            SamAccountName: workerId,
            DisplayName: $"Worker {workerId}",
            DistinguishedName: $"CN=Worker {workerId},OU=Graveyard,DC=example,DC=com",
            Status: "T",
            EndDateUtc: endDateUtc,
            LastObservedAtUtc: DateTimeOffset.Parse("2026-04-10T00:00:00Z"),
            Active: true,
            IsOnHold: isOnHold,
            HoldPlacedAtUtc: isOnHold ? DateTimeOffset.Parse("2026-04-05T00:00:00Z") : null,
            HoldPlacedBy: isOnHold ? "admin-1" : null);

    private static LifecyclePolicySettings CreateLifecycleSettings() =>
        new(
            ActiveOu: "OU=Employees,DC=example,DC=com",
            PrehireOu: "OU=Prehire,DC=example,DC=com",
            GraveyardOu: "OU=Graveyard,DC=example,DC=com",
            InactiveStatusField: "emplStatus",
            InactiveStatusValues: ["T"],
            DirectoryIdentityAttribute: "employeeID");

    private static IDirectoryGateway CreateDirectoryGateway(params string[] workerIds) =>
        new StubDirectoryGateway(
            workerIds.Select(workerId => new DirectoryUserSnapshot(
                SamAccountName: workerId,
                DistinguishedName: $"CN=Worker {workerId},OU=Graveyard,DC=example,DC=com",
                Enabled: false,
                DisplayName: $"Worker {workerId}",
                Attributes: new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
                {
                    ["employeeID"] = workerId
                })).ToArray());

    private sealed class StubGraveyardRetentionStore(IReadOnlyList<GraveyardRetentionRecord> records) : IGraveyardRetentionStore
    {
        public List<string> ResolvedWorkerIds { get; } = [];

        public Task UpsertObservedAsync(GraveyardRetentionRecord record, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task ResolveAsync(string workerId, CancellationToken cancellationToken)
        {
            ResolvedWorkerIds.Add(workerId);
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<GraveyardRetentionRecord>> ListActiveAsync(CancellationToken cancellationToken) =>
            Task.FromResult(records);

        public Task SetHoldAsync(string workerId, bool isOnHold, string? actingUserId, DateTimeOffset changedAtUtc, CancellationToken cancellationToken) =>
            Task.CompletedTask;

        public Task<GraveyardRetentionReportStatus> GetReportStatusAsync(CancellationToken cancellationToken) =>
            Task.FromResult(new GraveyardRetentionReportStatus(null, null, null));

        public Task RecordReportAttemptAsync(DateTimeOffset attemptedAt, string? error, DateTimeOffset? sentAtUtc, CancellationToken cancellationToken) =>
            Task.CompletedTask;
    }

    private sealed class StubDirectoryGateway(IReadOnlyList<DirectoryUserSnapshot> users) : IDirectoryGateway
    {
        public Task<DirectoryUserSnapshot?> FindByWorkerAsync(WorkerSnapshot worker, CancellationToken cancellationToken) =>
            Task.FromResult<DirectoryUserSnapshot?>(null);

        public Task<IReadOnlyList<DirectoryUserSnapshot>> ListUsersInOuAsync(string ouDistinguishedName, CancellationToken cancellationToken) =>
            Task.FromResult(users);

        public Task<string?> ResolveManagerDistinguishedNameAsync(string managerId, CancellationToken cancellationToken) =>
            Task.FromResult<string?>(null);

        public Task<string> ResolveAvailableEmailLocalPartAsync(WorkerSnapshot worker, bool isCreate, CancellationToken cancellationToken) =>
            Task.FromResult(worker.WorkerId.ToLowerInvariant());
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
        public int CompletedCalls { get; private set; }

        public int FailedCalls { get; private set; }

        public List<RunEntryRecord> Entries { get; } = [];

        public Task ExecutePlannedRunAsync(RunPlan plan, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task StartRunAsync(string runId, string mode, bool dryRun, string runTrigger, string? requestedBy, int totalWorkers, string? initialAction, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task RecordProgressAsync(string runId, string mode, bool dryRun, int processedWorkers, int totalWorkers, string? currentWorkerId, string? lastAction, RunTally tally, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task AppendRunEntryAsync(string runId, RunEntryRecord entry, CancellationToken cancellationToken)
        {
            Entries.Add(entry);
            return Task.CompletedTask;
        }

        public Task CompleteRunAsync(string runId, string mode, bool dryRun, int totalWorkers, RunTally tally, JsonElement report, DateTimeOffset startedAt, CancellationToken cancellationToken)
        {
            CompletedCalls++;
            return Task.CompletedTask;
        }

        public Task CancelRunAsync(string runId, string mode, bool dryRun, int processedWorkers, int totalWorkers, string? currentWorkerId, string? reason, RunTally tally, JsonElement report, DateTimeOffset startedAt, CancellationToken cancellationToken) =>
            Task.CompletedTask;

        public Task FailRunAsync(string runId, string mode, bool dryRun, int processedWorkers, int totalWorkers, string? currentWorkerId, string errorMessage, RunTally tally, JsonElement report, DateTimeOffset startedAt, CancellationToken cancellationToken)
        {
            FailedCalls++;
            return Task.CompletedTask;
        }
    }

    private sealed class FakeTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}

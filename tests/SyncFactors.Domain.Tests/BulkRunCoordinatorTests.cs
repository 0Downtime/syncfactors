using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using SyncFactors.Contracts;
using SyncFactors.Domain;

namespace SyncFactors.Domain.Tests;

public sealed class BulkRunCoordinatorTests
{
    [Fact]
    public async Task ExecuteAsync_ContinuesWhenWorkerPlanningFails()
    {
        CapturingRunLifecycleService.Entries.Clear();
        CapturingRunLifecycleService.Reset();
        WorkerSnapshot[] workers =
        [
            CreateWorker("10001"),
            CreateWorker("10002")
        ];
        var deltaSyncService = new CapturingDeltaSyncService();
        var coordinator = new BulkRunCoordinator(
            new StubWorkerSource(workers),
            deltaSyncService,
            new StubRunQueueStore(),
            new StubGraveyardRetentionStore(),
            new StubWorkerPlanningService(),
            new StubDirectoryMutationCommandBuilder(),
            new StubDirectoryCommandGateway(),
            new CapturingRunLifecycleService(),
            new WorkerRunSettings(MaxCreatesPerRun: 10),
            CreateLifecycleSettings(),
            NullLogger<BulkRunCoordinator>.Instance,
            TimeProvider.System);

        var runId = await coordinator.ExecuteAsync(
            new RunQueueRequest(
                RequestId: "req-1",
                Mode: "BulkSync",
                DryRun: true,
                RunTrigger: "AdHoc",
                RequestedBy: "test",
                Status: "Pending",
                RequestedAt: DateTimeOffset.UtcNow,
                StartedAt: null,
                CompletedAt: null,
                RunId: null,
                ErrorMessage: null),
            maxDegreeOfParallelism: 1,
            CancellationToken.None);

        Assert.StartsWith("bulk-", runId, StringComparison.Ordinal);
        Assert.Equal(2, CapturingRunLifecycleService.Entries.Count);
        Assert.Contains(CapturingRunLifecycleService.Entries, entry => entry.WorkerId == "10001" && entry.Bucket == "unchanged");
        Assert.Contains(CapturingRunLifecycleService.Entries, entry => entry.WorkerId == "10002" && entry.Bucket == "conflicts");
        Assert.Equal(0, deltaSyncService.RecordCalls);
    }

    [Fact]
    public async Task ExecuteAsync_LiveRun_AdvancesDeltaCheckpointAfterSuccess()
    {
        CapturingRunLifecycleService.Entries.Clear();
        CapturingRunLifecycleService.Reset();
        var deltaSyncService = new CapturingDeltaSyncService();
        var now = DateTimeOffset.Parse("2026-04-14T12:00:00Z");
        var coordinator = new BulkRunCoordinator(
            new StubWorkerSource([CreateWorker("10001")]),
            deltaSyncService,
            new StubRunQueueStore(),
            new StubGraveyardRetentionStore(),
            new StubWorkerPlanningService(),
            new StubDirectoryMutationCommandBuilder(),
            new SuccessfulDirectoryCommandGateway(),
            new CapturingRunLifecycleService(),
            new WorkerRunSettings(MaxCreatesPerRun: 10),
            CreateLifecycleSettings(),
            NullLogger<BulkRunCoordinator>.Instance,
            new FakeTimeProvider(now));

        await coordinator.ExecuteAsync(
            new RunQueueRequest(
                RequestId: "req-live",
                Mode: "BulkSync",
                DryRun: false,
                RunTrigger: "AdHoc",
                RequestedBy: "test",
                Status: "Pending",
                RequestedAt: DateTimeOffset.UtcNow,
                StartedAt: null,
                CompletedAt: null,
                RunId: null,
                ErrorMessage: null),
            maxDegreeOfParallelism: 1,
            CancellationToken.None);

        Assert.Equal(1, deltaSyncService.RecordCalls);
        Assert.Equal(now, deltaSyncService.LastCheckpointUtc);
    }

    [Fact]
    public async Task ExecuteAsync_LiveRun_DoesNotAdvanceDeltaCheckpointWhenRunHasConflicts()
    {
        CapturingRunLifecycleService.Entries.Clear();
        CapturingRunLifecycleService.Reset();
        var deltaSyncService = new CapturingDeltaSyncService();
        var coordinator = new BulkRunCoordinator(
            new StubWorkerSource([CreateWorker("10001")]),
            deltaSyncService,
            new StubRunQueueStore(),
            new StubGraveyardRetentionStore(),
            new StubWorkerPlanningService(includeChangedAttribute: true),
            new StubDirectoryMutationCommandBuilder(),
            new FailingDirectoryCommandGateway(),
            new CapturingRunLifecycleService(),
            new WorkerRunSettings(MaxCreatesPerRun: 10),
            CreateLifecycleSettings(),
            NullLogger<BulkRunCoordinator>.Instance,
            TimeProvider.System);

        await coordinator.ExecuteAsync(
            new RunQueueRequest(
                RequestId: "req-conflict",
                Mode: "BulkSync",
                DryRun: false,
                RunTrigger: "AdHoc",
                RequestedBy: "test",
                Status: "Pending",
                RequestedAt: DateTimeOffset.UtcNow,
                StartedAt: null,
                CompletedAt: null,
                RunId: null,
                ErrorMessage: null),
            maxDegreeOfParallelism: 1,
            CancellationToken.None);

        Assert.Equal(0, deltaSyncService.RecordCalls);
    }

    [Fact]
    public async Task ExecuteAsync_CreateGuardrailExceeded_FailsRunAndStopsProcessing()
    {
        CapturingRunLifecycleService.Entries.Clear();
        CapturingRunLifecycleService.Reset();
        var deltaSyncService = new CapturingDeltaSyncService();
        var coordinator = new BulkRunCoordinator(
            new StubWorkerSource([CreateWorker("10001"), CreateWorker("10002")]),
            deltaSyncService,
            new StubRunQueueStore(),
            new StubGraveyardRetentionStore(),
            new CreateWorkerPlanningService(),
            new StubDirectoryMutationCommandBuilder(),
            new SuccessfulDirectoryCommandGateway(),
            new CapturingRunLifecycleService(),
            new WorkerRunSettings(MaxCreatesPerRun: 1),
            CreateLifecycleSettings(),
            NullLogger<BulkRunCoordinator>.Instance,
            TimeProvider.System);

        var ex = await Assert.ThrowsAsync<GuardrailExceededException>(() => coordinator.ExecuteAsync(
            new RunQueueRequest(
                RequestId: "req-guardrail",
                Mode: "BulkSync",
                DryRun: true,
                RunTrigger: "AdHoc",
                RequestedBy: "test",
                Status: "Pending",
                RequestedAt: DateTimeOffset.UtcNow,
                StartedAt: null,
                CompletedAt: null,
                RunId: null,
                ErrorMessage: null),
            maxDegreeOfParallelism: 1,
            CancellationToken.None));

        Assert.Contains("Create guardrail exceeded", ex.Message, StringComparison.Ordinal);
        Assert.Equal(0, deltaSyncService.RecordCalls);
        Assert.Equal(0, CapturingRunLifecycleService.CompletedCalls);
        Assert.Equal(1, CapturingRunLifecycleService.FailedCalls);
        Assert.Contains(CapturingRunLifecycleService.Entries, entry => entry.WorkerId == "10002" && entry.Bucket == "guardrailFailures");
    }

    [Fact]
    public async Task ExecuteAsync_LiveRun_TracksObservedGraveyardUsers()
    {
        CapturingRunLifecycleService.Entries.Clear();
        CapturingRunLifecycleService.Reset();
        var retentionStore = new StubGraveyardRetentionStore();
        var coordinator = new BulkRunCoordinator(
            new StubWorkerSource([CreateWorker("10001", "64308", "/Date(1777772800000)/")]),
            new CapturingDeltaSyncService(),
            new StubRunQueueStore(),
            retentionStore,
            new GraveyardWorkerPlanningService(),
            new StubDirectoryMutationCommandBuilder(),
            new SuccessfulDirectoryCommandGateway(),
            new CapturingRunLifecycleService(),
            new WorkerRunSettings(MaxCreatesPerRun: 10, MaxDeletionsPerRun: 5),
            CreateLifecycleSettings(),
            NullLogger<BulkRunCoordinator>.Instance,
            TimeProvider.System);

        await coordinator.ExecuteAsync(
            new RunQueueRequest(
                RequestId: "req-delete",
                Mode: "BulkSync",
                DryRun: false,
                RunTrigger: "AdHoc",
                RequestedBy: "test",
                Status: "Pending",
                RequestedAt: DateTimeOffset.UtcNow,
                StartedAt: null,
                CompletedAt: null,
                RunId: null,
                ErrorMessage: null),
            maxDegreeOfParallelism: 1,
            CancellationToken.None);

        var record = Assert.Single(retentionStore.Observed);
        Assert.Equal("10001", record.WorkerId);
        Assert.Equal("64308", record.Status);
        Assert.Equal(DateTimeOffset.FromUnixTimeMilliseconds(1777772800000), record.EndDateUtc);
    }

    [Fact]
    public async Task ExecuteAsync_PersistsEmploymentStatusInEntryItem()
    {
        CapturingRunLifecycleService.Entries.Clear();
        CapturingRunLifecycleService.Reset();
        var coordinator = new BulkRunCoordinator(
            new StubWorkerSource([CreateWorker("10001", "64304")]),
            new CapturingDeltaSyncService(),
            new StubRunQueueStore(),
            new StubGraveyardRetentionStore(),
            new StubWorkerPlanningService(),
            new StubDirectoryMutationCommandBuilder(),
            new SuccessfulDirectoryCommandGateway(),
            new CapturingRunLifecycleService(),
            new WorkerRunSettings(MaxCreatesPerRun: 10),
            CreateLifecycleSettings(),
            NullLogger<BulkRunCoordinator>.Instance,
            TimeProvider.System);

        await coordinator.ExecuteAsync(
            new RunQueueRequest(
                RequestId: "req-status",
                Mode: "BulkSync",
                DryRun: true,
                RunTrigger: "AdHoc",
                RequestedBy: "test",
                Status: "Pending",
                RequestedAt: DateTimeOffset.UtcNow,
                StartedAt: null,
                CompletedAt: null,
                RunId: null,
                ErrorMessage: null),
            maxDegreeOfParallelism: 1,
            CancellationToken.None);

        var entry = Assert.Single(CapturingRunLifecycleService.Entries);
        Assert.Equal("64304", entry.Item.GetProperty("emplStatus").GetString());
    }

    private static WorkerSnapshot CreateWorker(string workerId, string? status = null, string? endDate = null)
    {
        var attributes = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        if (status is not null)
        {
            attributes["emplStatus"] = status;
        }

        if (endDate is not null)
        {
            attributes["endDate"] = endDate;
        }

        return new WorkerSnapshot(
            WorkerId: workerId,
            PreferredName: $"Worker{workerId}",
            LastName: "Sample",
            Department: "IT",
            TargetOu: "OU=LabUsers,DC=example,DC=com",
            IsPrehire: false,
            Attributes: attributes);
    }

    private static LifecyclePolicySettings CreateLifecycleSettings()
    {
        return new LifecyclePolicySettings(
            ActiveOu: "OU=LabUsers,DC=example,DC=com",
            PrehireOu: "OU=Prehire,DC=example,DC=com",
            GraveyardOu: "OU=Graveyard,DC=example,DC=com",
            InactiveStatusField: "emplStatus",
            InactiveStatusValues: ["64307", "64308"],
            LeaveOu: "OU=Leave Users,DC=example,DC=com",
            LeaveStatusValues: ["64303", "64304"],
            DirectoryIdentityAttribute: "employeeID");
    }

    private sealed class StubWorkerSource(IReadOnlyList<WorkerSnapshot> workers) : IWorkerSource
    {
        public Task<WorkerSnapshot?> GetWorkerAsync(string workerId, CancellationToken cancellationToken)
        {
            _ = cancellationToken;
            return Task.FromResult(workers.FirstOrDefault(worker => worker.WorkerId == workerId));
        }

        public async IAsyncEnumerable<WorkerSnapshot> ListWorkersAsync(WorkerListingMode mode, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
        {
            _ = mode;
            foreach (var worker in workers)
            {
                cancellationToken.ThrowIfCancellationRequested();
                yield return worker;
                await Task.Yield();
            }
        }
    }

    private sealed class StubDirectoryGateway(IReadOnlyList<DirectoryUserSnapshot>? graveyardUsers = null) : IDirectoryGateway
    {
        public Task<DirectoryUserSnapshot?> FindByWorkerAsync(WorkerSnapshot worker, CancellationToken cancellationToken) =>
            Task.FromResult<DirectoryUserSnapshot?>(null);

        public Task<IReadOnlyList<DirectoryUserSnapshot>> ListUsersInOuAsync(string ouDistinguishedName, CancellationToken cancellationToken)
        {
            _ = cancellationToken;
            if (!string.Equals(ouDistinguishedName, "OU=Graveyard,DC=example,DC=com", StringComparison.OrdinalIgnoreCase))
            {
                return Task.FromResult<IReadOnlyList<DirectoryUserSnapshot>>([]);
            }

            return Task.FromResult<IReadOnlyList<DirectoryUserSnapshot>>(graveyardUsers ?? []);
        }

        public Task<string?> ResolveManagerDistinguishedNameAsync(string managerId, CancellationToken cancellationToken) =>
            Task.FromResult<string?>(null);

        public Task<string> ResolveAvailableEmailLocalPartAsync(WorkerSnapshot worker, bool isCreate, CancellationToken cancellationToken) =>
            Task.FromResult(DirectoryIdentityFormatter.BuildBaseEmailLocalPart(worker.PreferredName, worker.LastName));
    }

    private sealed class StubWorkerPlanningService(bool includeChangedAttribute = false) : IWorkerPlanningService
    {
        public Task<PlannedWorkerAction> PlanAsync(WorkerSnapshot worker, string? logPath, CancellationToken cancellationToken)
        {
            _ = logPath;
            _ = cancellationToken;

            if (worker.WorkerId == "10002")
            {
                throw new InvalidOperationException("AD lookup timed out.");
            }

            return Task.FromResult(
                new PlannedWorkerAction(
                    Worker: worker,
                    DirectoryUser: new DirectoryUserSnapshot(null, null, null, null, new Dictionary<string, string?>()),
                    Identity: new IdentityMatchResult("updates", true, worker.WorkerId, null, null),
                    ManagerDistinguishedName: null,
                    ProposedEmailAddress: $"{worker.WorkerId}@example.com",
                    AttributeChanges: includeChangedAttribute
                        ? [new AttributeChange("department", "department", "(unset)", worker.Department, true)]
                        : [],
                    MissingSourceAttributes: [],
                    Bucket: "updates",
                    CurrentOu: worker.TargetOu,
                    TargetOu: worker.TargetOu,
                    CurrentEnabled: true,
                    TargetEnabled: true,
                    PrimaryAction: "UpdateUser",
                    Operations: [new DirectoryOperation("UpdateUser")],
                    ReviewCategory: null,
                    ReviewCaseType: null,
                    Reason: null,
                    CanAutoApply: true));
        }
    }

    private sealed class GraveyardWorkerPlanningService : IWorkerPlanningService
    {
        public Task<PlannedWorkerAction> PlanAsync(WorkerSnapshot worker, string? logPath, CancellationToken cancellationToken)
        {
            _ = logPath;
            _ = cancellationToken;

            return Task.FromResult(
                new PlannedWorkerAction(
                    Worker: worker,
                    DirectoryUser: new DirectoryUserSnapshot(
                        SamAccountName: worker.WorkerId,
                        DistinguishedName: $"CN={worker.WorkerId},OU=Employees,DC=example,DC=com",
                        Enabled: true,
                        DisplayName: $"Worker {worker.WorkerId}",
                        Attributes: new Dictionary<string, string?>()),
                    Identity: new IdentityMatchResult("graveyardMoves", true, worker.WorkerId, null, null),
                    ManagerDistinguishedName: null,
                    ProposedEmailAddress: $"{worker.WorkerId}@example.com",
                    AttributeChanges: [],
                    MissingSourceAttributes: [],
                    Bucket: "graveyardMoves",
                    CurrentOu: "OU=Employees,DC=example,DC=com",
                    TargetOu: "OU=Graveyard,DC=example,DC=com",
                    CurrentEnabled: true,
                    TargetEnabled: false,
                    PrimaryAction: "MoveUser",
                    Operations: [new DirectoryOperation("MoveUser", "OU=Graveyard,DC=example,DC=com"), new DirectoryOperation("DisableUser")],
                    ReviewCategory: null,
                    ReviewCaseType: null,
                    Reason: null,
                    CanAutoApply: true));
        }
    }

    private sealed class CreateWorkerPlanningService : IWorkerPlanningService
    {
        public Task<PlannedWorkerAction> PlanAsync(WorkerSnapshot worker, string? logPath, CancellationToken cancellationToken)
        {
            _ = logPath;
            _ = cancellationToken;

            return Task.FromResult(
                new PlannedWorkerAction(
                    Worker: worker,
                    DirectoryUser: new DirectoryUserSnapshot(null, null, null, null, new Dictionary<string, string?>()),
                    Identity: new IdentityMatchResult("creates", false, worker.WorkerId, null, null),
                    ManagerDistinguishedName: null,
                    ProposedEmailAddress: $"{worker.WorkerId}@example.com",
                    AttributeChanges: [new AttributeChange("displayName", "preferredName", "(unset)", worker.PreferredName, true)],
                    MissingSourceAttributes: [],
                    Bucket: "creates",
                    CurrentOu: string.Empty,
                    TargetOu: worker.TargetOu,
                    CurrentEnabled: null,
                    TargetEnabled: true,
                    PrimaryAction: "CreateUser",
                    Operations: [new DirectoryOperation("CreateUser", worker.TargetOu)],
                    ReviewCategory: null,
                    ReviewCaseType: null,
                    Reason: null,
                    CanAutoApply: true));
        }
    }

    private sealed class StubDirectoryMutationCommandBuilder : IDirectoryMutationCommandBuilder
    {
        public DirectoryMutationCommand Build(PlannedWorkerAction plan)
        {
            return new DirectoryMutationCommand("UpdateUser", plan.Worker.WorkerId, null, null, plan.Worker.WorkerId, plan.Worker.WorkerId, $"{plan.Worker.WorkerId}@example.com", $"{plan.Worker.WorkerId}@example.com", plan.Worker.TargetOu, plan.Worker.WorkerId, null, true, [new DirectoryOperation("UpdateUser")], new Dictionary<string, string?>());
        }

        public DirectoryMutationCommand Build(WorkerSnapshot worker, WorkerPreviewResult preview)
        {
            _ = preview;
            return new DirectoryMutationCommand("UpdateUser", worker.WorkerId, null, null, worker.WorkerId, worker.WorkerId, $"{worker.WorkerId}@example.com", $"{worker.WorkerId}@example.com", worker.TargetOu, worker.WorkerId, null, true, [new DirectoryOperation("UpdateUser")], new Dictionary<string, string?>());
        }
    }

    private sealed class StubDirectoryCommandGateway : IDirectoryCommandGateway
    {
        public Task<DirectoryCommandResult> ExecuteAsync(DirectoryMutationCommand command, CancellationToken cancellationToken)
        {
            _ = command;
            _ = cancellationToken;
            throw new InvalidOperationException("Should not execute for dry-run test.");
        }
    }

    private sealed class SuccessfulDirectoryCommandGateway : IDirectoryCommandGateway
    {
        public Task<DirectoryCommandResult> ExecuteAsync(DirectoryMutationCommand command, CancellationToken cancellationToken)
        {
            _ = cancellationToken;
            return Task.FromResult(new DirectoryCommandResult(true, command.Action, command.SamAccountName, null, "Applied", null));
        }
    }

    private sealed class CapturingDirectoryCommandGateway : IDirectoryCommandGateway
    {
        public List<DirectoryMutationCommand> Commands { get; } = [];

        public Task<DirectoryCommandResult> ExecuteAsync(DirectoryMutationCommand command, CancellationToken cancellationToken)
        {
            _ = cancellationToken;
            Commands.Add(command);
            return Task.FromResult(new DirectoryCommandResult(true, command.Action, command.SamAccountName, command.CurrentDistinguishedName, "Applied", null));
        }
    }

    private sealed class FailingDirectoryCommandGateway : IDirectoryCommandGateway
    {
        public Task<DirectoryCommandResult> ExecuteAsync(DirectoryMutationCommand command, CancellationToken cancellationToken)
        {
            _ = cancellationToken;
            return Task.FromResult(new DirectoryCommandResult(false, command.Action, command.SamAccountName, null, "Failed", null));
        }
    }

    private sealed class StubGraveyardRetentionStore : IGraveyardRetentionStore
    {
        public List<GraveyardRetentionRecord> Observed { get; } = [];
        public List<string> ResolvedWorkerIds { get; } = [];

        public Task UpsertObservedAsync(GraveyardRetentionRecord record, CancellationToken cancellationToken)
        {
            _ = cancellationToken;
            Observed.Add(record);
            return Task.CompletedTask;
        }

        public Task ResolveAsync(string workerId, CancellationToken cancellationToken)
        {
            _ = cancellationToken;
            ResolvedWorkerIds.Add(workerId);
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<GraveyardRetentionRecord>> ListActiveAsync(CancellationToken cancellationToken)
        {
            _ = cancellationToken;
            return Task.FromResult<IReadOnlyList<GraveyardRetentionRecord>>(Observed);
        }

        public Task SetHoldAsync(string workerId, bool isOnHold, string? actingUserId, DateTimeOffset changedAtUtc, CancellationToken cancellationToken)
        {
            _ = workerId;
            _ = isOnHold;
            _ = actingUserId;
            _ = changedAtUtc;
            _ = cancellationToken;
            return Task.CompletedTask;
        }

        public Task<GraveyardRetentionReportStatus> GetReportStatusAsync(CancellationToken cancellationToken)
        {
            _ = cancellationToken;
            return Task.FromResult(new GraveyardRetentionReportStatus(null, null, null));
        }

        public Task RecordReportAttemptAsync(DateTimeOffset attemptedAt, string? error, DateTimeOffset? sentAtUtc, CancellationToken cancellationToken)
        {
            _ = attemptedAt;
            _ = error;
            _ = sentAtUtc;
            _ = cancellationToken;
            return Task.CompletedTask;
        }
    }

    private sealed class StubRunQueueStore : IRunQueueStore
    {
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
            return Task.FromResult<RunQueueRequest?>(null);
        }

        public Task<bool> HasPendingOrActiveRunAsync(CancellationToken cancellationToken)
        {
            _ = cancellationToken;
            return Task.FromResult(false);
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

    private sealed class CapturingRunLifecycleService : IRunLifecycleService
    {
        public static List<RunEntryRecord> Entries { get; } = [];
        public static int CompletedCalls { get; private set; }
        public static int FailedCalls { get; private set; }

        public static void Reset()
        {
            CompletedCalls = 0;
            FailedCalls = 0;
        }

        public Task ExecutePlannedRunAsync(RunPlan plan, CancellationToken cancellationToken)
        {
            _ = plan;
            _ = cancellationToken;
            return Task.CompletedTask;
        }

        public Task StartRunAsync(string runId, string mode, bool dryRun, string runTrigger, string? requestedBy, int totalWorkers, string? initialAction, CancellationToken cancellationToken)
        {
            _ = runId;
            _ = mode;
            _ = dryRun;
            _ = runTrigger;
            _ = requestedBy;
            _ = totalWorkers;
            _ = initialAction;
            _ = cancellationToken;
            Entries.Clear();
            return Task.CompletedTask;
        }

        public Task RecordProgressAsync(string runId, string mode, bool dryRun, int processedWorkers, int totalWorkers, string? currentWorkerId, string? lastAction, RunTally tally, CancellationToken cancellationToken)
        {
            _ = runId;
            _ = mode;
            _ = dryRun;
            _ = processedWorkers;
            _ = totalWorkers;
            _ = currentWorkerId;
            _ = lastAction;
            _ = tally;
            _ = cancellationToken;
            return Task.CompletedTask;
        }

        public Task AppendRunEntryAsync(string runId, RunEntryRecord entry, CancellationToken cancellationToken)
        {
            _ = runId;
            _ = cancellationToken;
            Entries.Add(entry);
            return Task.CompletedTask;
        }

        public Task CompleteRunAsync(string runId, string mode, bool dryRun, int totalWorkers, RunTally tally, JsonElement report, DateTimeOffset startedAt, CancellationToken cancellationToken)
        {
            _ = runId;
            _ = mode;
            _ = dryRun;
            _ = totalWorkers;
            _ = tally;
            _ = report;
            _ = startedAt;
            _ = cancellationToken;
            CompletedCalls++;
            return Task.CompletedTask;
        }

        public Task CancelRunAsync(string runId, string mode, bool dryRun, int processedWorkers, int totalWorkers, string? currentWorkerId, string? reason, RunTally tally, JsonElement report, DateTimeOffset startedAt, CancellationToken cancellationToken)
        {
            throw new InvalidOperationException("Bulk run should not be canceled in this test.");
        }

        public Task FailRunAsync(string runId, string mode, bool dryRun, int processedWorkers, int totalWorkers, string? currentWorkerId, string errorMessage, RunTally tally, JsonElement report, DateTimeOffset startedAt, CancellationToken cancellationToken)
        {
            _ = runId;
            _ = mode;
            _ = dryRun;
            _ = processedWorkers;
            _ = totalWorkers;
            _ = currentWorkerId;
            _ = errorMessage;
            _ = tally;
            _ = report;
            _ = startedAt;
            _ = cancellationToken;
            FailedCalls++;
            return Task.CompletedTask;
        }
    }

    private sealed class CapturingDeltaSyncService : IDeltaSyncService
    {
        public int RecordCalls { get; private set; }
        public DateTimeOffset? LastCheckpointUtc { get; private set; }

        public Task<DeltaSyncWindow> GetWindowAsync(CancellationToken cancellationToken)
        {
            _ = cancellationToken;
            return Task.FromResult(new DeltaSyncWindow(false, false, null, string.Empty, null, null));
        }

        public Task RecordSuccessfulRunAsync(DateTimeOffset checkpointUtc, CancellationToken cancellationToken)
        {
            _ = cancellationToken;
            LastCheckpointUtc = checkpointUtc;
            RecordCalls++;
            return Task.CompletedTask;
        }
    }

    private sealed class FakeTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => utcNow;
    }
}

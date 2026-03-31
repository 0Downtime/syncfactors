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
        WorkerSnapshot[] workers =
        [
            CreateWorker("10001"),
            CreateWorker("10002")
        ];
        var coordinator = new BulkRunCoordinator(
            new StubWorkerSource(workers),
            new StubRunQueueStore(),
            new StubWorkerPlanningService(),
            new StubDirectoryMutationCommandBuilder(),
            new StubDirectoryCommandGateway(),
            new CapturingRunLifecycleService(),
            new WorkerRunSettings(MaxCreatesPerRun: 10),
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
    }

    private static WorkerSnapshot CreateWorker(string workerId)
    {
        return new WorkerSnapshot(
            WorkerId: workerId,
            PreferredName: $"Worker{workerId}",
            LastName: "Sample",
            Department: "IT",
            TargetOu: "OU=LabUsers,DC=example,DC=com",
            IsPrehire: false,
            Attributes: new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase));
    }

    private sealed class StubWorkerSource(IReadOnlyList<WorkerSnapshot> workers) : IWorkerSource
    {
        public Task<WorkerSnapshot?> GetWorkerAsync(string workerId, CancellationToken cancellationToken)
        {
            _ = cancellationToken;
            return Task.FromResult(workers.FirstOrDefault(worker => worker.WorkerId == workerId));
        }

        public async IAsyncEnumerable<WorkerSnapshot> ListWorkersAsync([System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
        {
            foreach (var worker in workers)
            {
                cancellationToken.ThrowIfCancellationRequested();
                yield return worker;
                await Task.Yield();
            }
        }
    }

    private sealed class StubWorkerPlanningService : IWorkerPlanningService
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
                    AttributeChanges: [],
                    MissingSourceAttributes: [],
                    Bucket: "updates",
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
            return new DirectoryMutationCommand("UpdateUser", plan.Worker.WorkerId, null, null, plan.Worker.WorkerId, $"{plan.Worker.WorkerId}@example.com", $"{plan.Worker.WorkerId}@example.com", plan.Worker.TargetOu, plan.Worker.WorkerId, true, new Dictionary<string, string?>());
        }

        public DirectoryMutationCommand Build(WorkerSnapshot worker, WorkerPreviewResult preview)
        {
            _ = preview;
            return new DirectoryMutationCommand("UpdateUser", worker.WorkerId, null, null, worker.WorkerId, $"{worker.WorkerId}@example.com", $"{worker.WorkerId}@example.com", worker.TargetOu, worker.WorkerId, true, new Dictionary<string, string?>());
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
            return Task.CompletedTask;
        }

        public Task CancelRunAsync(string runId, string mode, bool dryRun, int processedWorkers, int totalWorkers, string? currentWorkerId, string? reason, RunTally tally, JsonElement report, DateTimeOffset startedAt, CancellationToken cancellationToken)
        {
            throw new InvalidOperationException("Bulk run should not be canceled in this test.");
        }

        public Task FailRunAsync(string runId, string mode, bool dryRun, int processedWorkers, int totalWorkers, string? currentWorkerId, string errorMessage, RunTally tally, JsonElement report, DateTimeOffset startedAt, CancellationToken cancellationToken)
        {
            throw new InvalidOperationException("Bulk run should not fail for a single planning exception.");
        }
    }
}

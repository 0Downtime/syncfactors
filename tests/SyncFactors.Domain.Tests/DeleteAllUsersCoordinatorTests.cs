using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using SyncFactors.Contracts;
using SyncFactors.Domain;

namespace SyncFactors.Domain.Tests;

public sealed class DeleteAllUsersCoordinatorTests
{
    [Fact]
    public async Task ExecuteAsync_DryRun_RecordsDeletionEntriesFromConfiguredOus()
    {
        CapturingRunLifecycleService.Reset();
        var coordinator = CreateCoordinator(
            directoryGateway: new StubDirectoryGateway(new Dictionary<string, IReadOnlyList<DirectoryUserSnapshot>>(StringComparer.OrdinalIgnoreCase)
            {
                ["OU=LabUsers,DC=example,DC=com"] =
                [
                    CreateDirectoryUser("10001", "lab10001", "OU=LabUsers,DC=example,DC=com")
                ],
                ["OU=Prehire,DC=example,DC=com"] =
                [
                    CreateDirectoryUser("10002", "lab10002", "OU=Prehire,DC=example,DC=com")
                ]
            }),
            commandGateway: new ThrowingDirectoryCommandGateway());

        var runId = await coordinator.ExecuteAsync(
            new RunQueueRequest("req-1", "DeleteAllUsers", true, "DeleteAllUsers", "test", "Pending", DateTimeOffset.UtcNow, null, null, null, null),
            CancellationToken.None);

        Assert.StartsWith("delete-all-", runId, StringComparison.Ordinal);
        Assert.Equal(2, CapturingRunLifecycleService.Entries.Count);
        Assert.All(CapturingRunLifecycleService.Entries, entry => Assert.Equal("deletions", entry.Bucket));
        Assert.Contains(CapturingRunLifecycleService.Entries, entry => entry.WorkerId == "10001" && entry.SamAccountName == "lab10001");
        Assert.Contains(CapturingRunLifecycleService.Entries, entry => entry.WorkerId == "10002" && entry.SamAccountName == "lab10002");
        Assert.Equal(1, CapturingRunLifecycleService.CompletedCalls);
        Assert.Equal(0, CapturingRunLifecycleService.FailedCalls);
    }

    [Fact]
    public async Task ExecuteAsync_LiveRun_ExecutesDeleteCommandForDistinctDirectoryUsers()
    {
        CapturingRunLifecycleService.Reset();
        var gateway = new CapturingDirectoryCommandGateway();
        var coordinator = CreateCoordinator(
            directoryGateway: new StubDirectoryGateway(new Dictionary<string, IReadOnlyList<DirectoryUserSnapshot>>(StringComparer.OrdinalIgnoreCase)
            {
                ["OU=LabUsers,DC=example,DC=com"] =
                [
                    CreateDirectoryUser("10001", "lab10001", "OU=LabUsers,DC=example,DC=com"),
                    CreateDirectoryUser("10002", "lab10002", "OU=LabUsers,DC=example,DC=com")
                ],
                ["OU=Leave,DC=example,DC=com"] =
                [
                    CreateDirectoryUser("10001", "lab10001", "OU=LabUsers,DC=example,DC=com")
                ]
            }),
            commandGateway: gateway);

        await coordinator.ExecuteAsync(
            new RunQueueRequest("req-live", "DeleteAllUsers", false, "DeleteAllUsers", "test", "Pending", DateTimeOffset.UtcNow, null, null, null, null),
            CancellationToken.None);

        Assert.Equal(2, gateway.Commands.Count);
        Assert.All(gateway.Commands, command => Assert.Equal("DeleteUser", command.Action));
        Assert.Equal(["10001", "10002"], gateway.Commands.Select(command => command.WorkerId).OrderBy(value => value, StringComparer.Ordinal).ToArray());
    }

    [Fact]
    public async Task ExecuteAsync_DeletionGuardrailExceeded_FailsRun()
    {
        CapturingRunLifecycleService.Reset();
        var coordinator = CreateCoordinator(
            directoryGateway: new StubDirectoryGateway(new Dictionary<string, IReadOnlyList<DirectoryUserSnapshot>>(StringComparer.OrdinalIgnoreCase)
            {
                ["OU=LabUsers,DC=example,DC=com"] =
                [
                    CreateDirectoryUser("10001", "lab10001", "OU=LabUsers,DC=example,DC=com"),
                    CreateDirectoryUser("10002", "lab10002", "OU=LabUsers,DC=example,DC=com")
                ]
            }),
            commandGateway: new ThrowingDirectoryCommandGateway(),
            workerRunSettings: new WorkerRunSettings(MaxCreatesPerRun: 10, MaxDisablesPerRun: 10, MaxDeletionsPerRun: 1));

        var ex = await Assert.ThrowsAsync<GuardrailExceededException>(() => coordinator.ExecuteAsync(
            new RunQueueRequest("req-guardrail", "DeleteAllUsers", true, "DeleteAllUsers", "test", "Pending", DateTimeOffset.UtcNow, null, null, null, null),
            CancellationToken.None));

        Assert.Contains("Deletion guardrail exceeded", ex.Message, StringComparison.Ordinal);
        Assert.Equal(1, CapturingRunLifecycleService.FailedCalls);
        Assert.Contains(CapturingRunLifecycleService.Entries, entry => entry.WorkerId == "10002" && entry.Bucket == "guardrailFailures");
    }

    private static DeleteAllUsersCoordinator CreateCoordinator(
        IDirectoryGateway directoryGateway,
        IDirectoryCommandGateway commandGateway,
        WorkerRunSettings? workerRunSettings = null)
    {
        return new DeleteAllUsersCoordinator(
            new StubRunQueueStore(),
            directoryGateway,
            commandGateway,
            new CapturingRunLifecycleService(),
            new LifecyclePolicySettings(
                ActiveOu: "OU=LabUsers,DC=example,DC=com",
                PrehireOu: "OU=Prehire,DC=example,DC=com",
                GraveyardOu: "OU=Graveyard,DC=example,DC=com",
                InactiveStatusField: "emplStatus",
                InactiveStatusValues: [],
                LeaveOu: "OU=Leave,DC=example,DC=com",
                LeaveStatusValues: [],
                DirectoryIdentityAttribute: "employeeID"),
            workerRunSettings ?? new WorkerRunSettings(MaxCreatesPerRun: 10, MaxDisablesPerRun: 10, MaxDeletionsPerRun: 10),
            NullLogger<DeleteAllUsersCoordinator>.Instance,
            TimeProvider.System);
    }

    private static DirectoryUserSnapshot CreateDirectoryUser(string workerId, string samAccountName, string ou)
    {
        return new DirectoryUserSnapshot(
            SamAccountName: samAccountName,
            DistinguishedName: $"CN={samAccountName},{ou}",
            Enabled: true,
            DisplayName: samAccountName,
            Attributes: new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
            {
                ["employeeID"] = workerId,
                ["UserPrincipalName"] = $"{samAccountName}@example.com",
                ["mail"] = $"{samAccountName}@example.com"
            });
    }

    private sealed class StubDirectoryGateway(IReadOnlyDictionary<string, IReadOnlyList<DirectoryUserSnapshot>> usersByOu) : IDirectoryGateway
    {
        public Task<DirectoryUserSnapshot?> FindByWorkerAsync(WorkerSnapshot worker, CancellationToken cancellationToken)
        {
            _ = worker;
            _ = cancellationToken;
            throw new InvalidOperationException("Delete-all reset should not look up users by worker.");
        }

        public Task<IReadOnlyList<DirectoryUserSnapshot>> ListUsersInOuAsync(string ouDistinguishedName, CancellationToken cancellationToken)
        {
            _ = cancellationToken;
            return Task.FromResult(usersByOu.TryGetValue(ouDistinguishedName, out var users)
                ? users
                : Array.Empty<DirectoryUserSnapshot>() as IReadOnlyList<DirectoryUserSnapshot>);
        }

        public Task<string?> ResolveManagerDistinguishedNameAsync(string managerId, CancellationToken cancellationToken)
        {
            _ = managerId;
            _ = cancellationToken;
            return Task.FromResult<string?>(null);
        }

        public Task<string> ResolveAvailableEmailLocalPartAsync(WorkerSnapshot worker, bool isCreate, CancellationToken cancellationToken)
        {
            _ = worker;
            _ = isCreate;
            _ = cancellationToken;
            return Task.FromResult(worker.WorkerId.ToLowerInvariant());
        }
    }

    private sealed class ThrowingDirectoryCommandGateway : IDirectoryCommandGateway
    {
        public Task<DirectoryCommandResult> ExecuteAsync(DirectoryMutationCommand command, CancellationToken cancellationToken)
        {
            _ = command;
            _ = cancellationToken;
            throw new InvalidOperationException("Delete commands should not execute during dry run.");
        }
    }

    private sealed class CapturingDirectoryCommandGateway : IDirectoryCommandGateway
    {
        public List<DirectoryMutationCommand> Commands { get; } = [];

        public Task<DirectoryCommandResult> ExecuteAsync(DirectoryMutationCommand command, CancellationToken cancellationToken)
        {
            _ = cancellationToken;
            Commands.Add(command);
            return Task.FromResult(new DirectoryCommandResult(true, command.Action, command.SamAccountName, command.CurrentDistinguishedName, "Deleted", null));
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
            Entries.Clear();
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
            throw new InvalidOperationException("Delete-all run should not be canceled in this test.");
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
}

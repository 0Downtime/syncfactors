using Microsoft.Extensions.Logging.Abstractions;
using SyncFactors.Contracts;
using SyncFactors.Domain;
namespace SyncFactors.Domain.Tests;

public sealed class ApplyPreviewServiceTests
{
    [Fact]
    public async Task ApplyAsync_PassesPreviewAttributesIntoDirectoryMutationCommand()
    {
        var worker = new WorkerSnapshot(
            WorkerId: "mock-10001",
            PreferredName: "Winnie",
            LastName: "Sample101",
            Department: "IT",
            TargetOu: "OU=LabUsers,DC=example,DC=com",
            IsPrehire: false,
            Attributes: new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
            {
                ["managerId"] = "mock-90001"
            });

        var preview = new WorkerPreviewResult(
            ReportPath: null,
            RunId: null,
            Mode: "Preview",
            Status: "Planned",
            ErrorMessage: null,
            ArtifactType: "WorkerPreview",
            SuccessFactorsAuth: "NativeScaffold",
            WorkerId: worker.WorkerId,
            Buckets: ["creates"],
            MatchedExistingUser: false,
            ReviewCategory: null,
            ReviewCaseType: null,
            Reason: null,
            OperatorActionSummary: null,
            SamAccountName: "mock-10001",
            ManagerDistinguishedName: null,
            TargetOu: worker.TargetOu,
            CurrentDistinguishedName: null,
            CurrentEnabled: null,
            ProposedEnable: true,
            OperationSummary: null,
            DiffRows:
            [
                new DiffRow("employeeID", "personIdExternal", "(unset)", "mock-10001", true),
                new DiffRow("GivenName", "firstName", "(unset)", "Winnie", true),
                new DiffRow("department", "department", "(unset)", "Information Technology", true),
                new DiffRow("extensionAttribute2", "businessUnit", "(unset)", "(unset)", true)
            ],
            SourceAttributes: [],
            Entries: []);

        var directoryCommandGateway = new CapturingDirectoryCommandGateway();
        var service = new ApplyPreviewService(
            new StubWorkerSource(worker),
            new StubWorkerPreviewPlanner(preview),
            new StubDirectoryGateway("winnie.sample101"),
            directoryCommandGateway,
            new StubRunRepository(),
            new StubRuntimeStatusStore(),
            NullLogger<ApplyPreviewService>.Instance);

        await service.ApplyAsync(worker.WorkerId, CancellationToken.None);

        var command = Assert.IsType<DirectoryMutationCommand>(directoryCommandGateway.LastCommand);
        Assert.Equal("CreateUser", command.Action);
        Assert.Equal("mock-10001", command.Attributes["employeeID"]);
        Assert.Equal("Winnie", command.Attributes["GivenName"]);
        Assert.Equal("Information Technology", command.Attributes["department"]);
        Assert.Null(command.Attributes["extensionAttribute2"]);
        Assert.Equal("Sample101, Winnie", command.Attributes["displayName"]);
        Assert.Equal("winnie.sample101@spireenergy.com", command.Attributes["userPrincipalName"]);
        Assert.Equal("winnie.sample101@spireenergy.com", command.Attributes["mail"]);
    }

    [Fact]
    public async Task ApplyAsync_PropagatesDirectoryMutationFailures()
    {
        var worker = new WorkerSnapshot(
            WorkerId: "mock-10001",
            PreferredName: "Winnie",
            LastName: "Sample101",
            Department: "IT",
            TargetOu: "OU=LabUsers,DC=example,DC=com",
            IsPrehire: false,
            Attributes: new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase));

        var preview = new WorkerPreviewResult(
            ReportPath: null,
            RunId: null,
            Mode: "Preview",
            Status: "Planned",
            ErrorMessage: null,
            ArtifactType: "WorkerPreview",
            SuccessFactorsAuth: "NativeScaffold",
            WorkerId: worker.WorkerId,
            Buckets: ["creates"],
            MatchedExistingUser: false,
            ReviewCategory: null,
            ReviewCaseType: null,
            Reason: null,
            OperatorActionSummary: null,
            SamAccountName: "mock-10001",
            ManagerDistinguishedName: null,
            TargetOu: worker.TargetOu,
            CurrentDistinguishedName: null,
            CurrentEnabled: null,
            ProposedEnable: true,
            OperationSummary: null,
            DiffRows: [],
            SourceAttributes: [],
            Entries: []);

        var service = new ApplyPreviewService(
            new StubWorkerSource(worker),
            new StubWorkerPreviewPlanner(preview),
            new StubDirectoryGateway("winnie.sample101"),
            new ThrowingDirectoryCommandGateway(new InvalidOperationException("LDAP bind failed.")),
            new StubRunRepository(),
            new StubRuntimeStatusStore(),
            NullLogger<ApplyPreviewService>.Instance);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => service.ApplyAsync(worker.WorkerId, CancellationToken.None));
        Assert.Equal("LDAP bind failed.", exception.Message);
    }

    private sealed class StubWorkerSource(WorkerSnapshot worker) : IWorkerSource
    {
        public Task<WorkerSnapshot?> GetWorkerAsync(string workerId, CancellationToken cancellationToken)
        {
            _ = workerId;
            _ = cancellationToken;
            return Task.FromResult<WorkerSnapshot?>(worker);
        }
    }

    private sealed class StubWorkerPreviewPlanner(WorkerPreviewResult preview) : IWorkerPreviewPlanner
    {
        public Task<WorkerPreviewResult> PreviewAsync(string workerId, CancellationToken cancellationToken)
        {
            _ = workerId;
            _ = cancellationToken;
            return Task.FromResult(preview);
        }
    }

    private sealed class StubDirectoryGateway(string emailLocalPart) : IDirectoryGateway
    {
        public Task<DirectoryUserSnapshot?> FindByWorkerAsync(WorkerSnapshot worker, CancellationToken cancellationToken)
        {
            _ = worker;
            _ = cancellationToken;
            return Task.FromResult<DirectoryUserSnapshot?>(null);
        }

        public Task<string?> ResolveManagerDistinguishedNameAsync(string managerId, CancellationToken cancellationToken)
        {
            _ = managerId;
            _ = cancellationToken;
            return Task.FromResult<string?>("CN=Manager,OU=LabUsers,DC=example,DC=com");
        }

        public Task<string> ResolveAvailableEmailLocalPartAsync(WorkerSnapshot worker, CancellationToken cancellationToken)
        {
            _ = worker;
            _ = cancellationToken;
            return Task.FromResult(emailLocalPart);
        }
    }

    private sealed class CapturingDirectoryCommandGateway : IDirectoryCommandGateway
    {
        public DirectoryMutationCommand? LastCommand { get; private set; }

        public Task<DirectoryCommandResult> ExecuteAsync(DirectoryMutationCommand command, CancellationToken cancellationToken)
        {
            _ = cancellationToken;
            LastCommand = command;
            return Task.FromResult(new DirectoryCommandResult(true, command.Action, command.SamAccountName, null, "ok", null));
        }
    }

    private sealed class ThrowingDirectoryCommandGateway(Exception exception) : IDirectoryCommandGateway
    {
        public Task<DirectoryCommandResult> ExecuteAsync(DirectoryMutationCommand command, CancellationToken cancellationToken)
        {
            _ = command;
            _ = cancellationToken;
            return Task.FromException<DirectoryCommandResult>(exception);
        }
    }

    private sealed class StubRunRepository : IRunRepository
    {
        public Task<IReadOnlyList<RunSummary>> ListRunsAsync(CancellationToken cancellationToken)
        {
            _ = cancellationToken;
            return Task.FromResult<IReadOnlyList<RunSummary>>([]);
        }

        public Task<RunDetail?> GetRunAsync(string runId, CancellationToken cancellationToken)
        {
            _ = runId;
            _ = cancellationToken;
            return Task.FromResult<RunDetail?>(null);
        }

        public Task SaveRunAsync(RunRecord run, CancellationToken cancellationToken)
        {
            _ = run;
            _ = cancellationToken;
            return Task.CompletedTask;
        }

        public Task ReplaceRunEntriesAsync(string runId, IReadOnlyList<RunEntryRecord> entries, CancellationToken cancellationToken)
        {
            _ = runId;
            _ = entries;
            _ = cancellationToken;
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<RunEntry>> GetRunEntriesAsync(string runId, string? bucket, string? workerId, string? reason, string? filter, string? entryId, CancellationToken cancellationToken)
        {
            _ = runId;
            _ = bucket;
            _ = workerId;
            _ = reason;
            _ = filter;
            _ = entryId;
            _ = cancellationToken;
            return Task.FromResult<IReadOnlyList<RunEntry>>([]);
        }
    }

    private sealed class StubRuntimeStatusStore : IRuntimeStatusStore
    {
        public Task<RuntimeStatus?> GetCurrentAsync(CancellationToken cancellationToken)
        {
            _ = cancellationToken;
            return Task.FromResult<RuntimeStatus?>(null);
        }

        public Task SaveAsync(RuntimeStatus status, CancellationToken cancellationToken)
        {
            _ = status;
            _ = cancellationToken;
            return Task.CompletedTask;
        }
    }
}

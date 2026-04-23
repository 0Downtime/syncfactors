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
            WorkerId: "10001",
            PreferredName: "Different",
            LastName: "Name",
            Department: "IT",
            TargetOu: "OU=LabUsers,DC=example,DC=com",
            IsPrehire: false,
            Attributes: new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
            {
                ["managerId"] = "90001"
            });

        var preview = new WorkerPreviewResult(
            ReportPath: null,
            RunId: "preview-10001-1",
            PreviousRunId: null,
            Fingerprint: "fingerprint-1",
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
            SamAccountName: "10001",
            ManagerDistinguishedName: "CN=Manager,OU=LabUsers,DC=example,DC=com",
            TargetOu: worker.TargetOu,
            CurrentDistinguishedName: null,
            CurrentEnabled: null,
            ProposedEnable: true,
            OperationSummary: null,
            DiffRows:
            [
                new DiffRow("employeeID", "personIdExternal", "(unset)", "10001", true),
                new DiffRow("GivenName", "firstName", "(unset)", "Winnie", true),
                new DiffRow("displayName", "preferredName,lastName", "(unset)", "Sample101, Winnie", true),
                new DiffRow("UserPrincipalName", "resolved email local-part", "(unset)", "preview.email@Exampleenergy.com", true),
                new DiffRow("mail", "resolved email local-part", "(unset)", "preview.email@Exampleenergy.com", true),
                new DiffRow("department", "department", "(unset)", "Information Technology", true),
                new DiffRow("extensionAttribute2", "businessUnit", "(unset)", "(unset)", true)
            ],
            SourceAttributes: [],
            UsedSourceAttributes: [],
            UnusedSourceAttributes: [],
            MissingSourceAttributes: [],
            Entries: []);

        var directoryCommandGateway = new CapturingDirectoryCommandGateway();
        var service = new ApplyPreviewService(
            new StubWorkerSource(worker),
            new DirectoryMutationCommandBuilder(),
            directoryCommandGateway,
            new StubRunRepository(preview),
            new StubRuntimeStatusStore(),
            new RealSyncSettings(),
            NullLogger<ApplyPreviewService>.Instance);

        await service.ApplyAsync(
            new ApplyPreviewRequest(
                WorkerId: worker.WorkerId,
                PreviewRunId: preview.RunId!,
                PreviewFingerprint: preview.Fingerprint,
                AcknowledgeRealSync: true),
            CancellationToken.None);

        var command = Assert.IsType<DirectoryMutationCommand>(directoryCommandGateway.LastCommand);
        Assert.Equal("CreateUser", command.Action);
        Assert.Equal("10001", command.Attributes["employeeID"]);
        Assert.Equal("Winnie", command.Attributes["GivenName"]);
        Assert.Equal("Information Technology", command.Attributes["department"]);
        Assert.Null(command.Attributes["extensionAttribute2"]);
        Assert.Equal("Sample101, Winnie", command.Attributes["displayName"]);
        Assert.Equal("preview.email@Exampleenergy.com", command.Attributes["UserPrincipalName"]);
        Assert.Equal("preview.email@Exampleenergy.com", command.Attributes["mail"]);
        Assert.Equal("10001", command.CommonName);
        Assert.Equal("Sample101, Winnie", command.DisplayName);
        Assert.Equal("preview.email@Exampleenergy.com", command.UserPrincipalName);
        Assert.Equal("preview.email@Exampleenergy.com", command.Mail);
        Assert.Equal("CN=Manager,OU=LabUsers,DC=example,DC=com", command.ManagerDistinguishedName);
    }

    [Fact]
    public async Task ApplyAsync_PersistsPrimaryEnableBucketFromPreview()
    {
        var worker = new WorkerSnapshot(
            WorkerId: "10001",
            PreferredName: "Winnie",
            LastName: "Sample101",
            Department: "IT",
            TargetOu: "OU=LabUsers,DC=example,DC=com",
            IsPrehire: false,
            Attributes: new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase));

        var preview = new WorkerPreviewResult(
            ReportPath: null,
            RunId: "preview-10001-enable",
            PreviousRunId: null,
            Fingerprint: "fingerprint-enable",
            Mode: "Preview",
            Status: "Planned",
            ErrorMessage: null,
            ArtifactType: "WorkerPreview",
            SuccessFactorsAuth: "NativeScaffold",
            WorkerId: worker.WorkerId,
            Buckets: ["enables"],
            MatchedExistingUser: true,
            ReviewCategory: null,
            ReviewCaseType: null,
            Reason: null,
            OperatorActionSummary: null,
            SamAccountName: "10001",
            ManagerDistinguishedName: null,
            TargetOu: worker.TargetOu,
            CurrentDistinguishedName: "CN=Sample101\\, Winnie,OU=LabUsers,DC=example,DC=com",
            CurrentEnabled: false,
            ProposedEnable: true,
            OperationSummary: null,
            DiffRows:
            [
                new DiffRow("UserPrincipalName", "resolved email local-part", "preview.email@Exampleenergy.com", "preview.email@Exampleenergy.com", false)
            ],
            SourceAttributes: [],
            UsedSourceAttributes: [],
            UnusedSourceAttributes: [],
            MissingSourceAttributes: [],
            Entries: []);

        var runRepository = new CapturingRunRepository(preview);
        var service = new ApplyPreviewService(
            new StubWorkerSource(worker),
            new DirectoryMutationCommandBuilder(),
            new CapturingDirectoryCommandGateway(),
            runRepository,
            new StubRuntimeStatusStore(),
            new RealSyncSettings(),
            NullLogger<ApplyPreviewService>.Instance);

        await service.ApplyAsync(
            new ApplyPreviewRequest(
                WorkerId: worker.WorkerId,
                PreviewRunId: preview.RunId!,
                PreviewFingerprint: preview.Fingerprint,
                AcknowledgeRealSync: true),
            CancellationToken.None);

        Assert.Single(runRepository.SavedRuns);
        Assert.Equal(1, runRepository.SavedRuns[0].Enables);
        Assert.Equal(0, runRepository.SavedRuns[0].Updates);
        Assert.Single(runRepository.ReplacedEntries);
        Assert.Equal("enables", runRepository.ReplacedEntries[0].entries.Single().Bucket);
    }

    [Fact]
    public async Task ApplyAsync_PropagatesDirectoryMutationFailures()
    {
        var worker = new WorkerSnapshot(
            WorkerId: "10001",
            PreferredName: "Winnie",
            LastName: "Sample101",
            Department: "IT",
            TargetOu: "OU=LabUsers,DC=example,DC=com",
            IsPrehire: false,
            Attributes: new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase));

        var preview = new WorkerPreviewResult(
            ReportPath: null,
            RunId: "preview-10001-2",
            PreviousRunId: null,
            Fingerprint: "fingerprint-2",
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
            SamAccountName: "10001",
            ManagerDistinguishedName: null,
            TargetOu: worker.TargetOu,
            CurrentDistinguishedName: null,
            CurrentEnabled: null,
            ProposedEnable: true,
            OperationSummary: null,
            DiffRows:
            [
                new DiffRow("UserPrincipalName", "resolved email local-part", "(unset)", "preview.email@Exampleenergy.com", true)
            ],
            SourceAttributes: [],
            UsedSourceAttributes: [],
            UnusedSourceAttributes: [],
            MissingSourceAttributes: [],
            Entries: []);

        var runtimeStatusStore = new CapturingRuntimeStatusStore();
        var runRepository = new CapturingRunRepository(preview);
        var service = new ApplyPreviewService(
            new StubWorkerSource(worker),
            new DirectoryMutationCommandBuilder(),
            new ThrowingDirectoryCommandGateway(new InvalidOperationException("LDAP bind failed.")),
            runRepository,
            runtimeStatusStore,
            new RealSyncSettings(),
            NullLogger<ApplyPreviewService>.Instance);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => service.ApplyAsync(
            new ApplyPreviewRequest(
                WorkerId: worker.WorkerId,
                PreviewRunId: preview.RunId!,
                PreviewFingerprint: preview.Fingerprint,
                AcknowledgeRealSync: true),
            CancellationToken.None));
        Assert.Equal("LDAP bind failed.", exception.Message);
        Assert.Equal(2, runtimeStatusStore.SavedStatuses.Count);
        Assert.Equal("InProgress", runtimeStatusStore.SavedStatuses[0].Status);
        Assert.Equal("Failed", runtimeStatusStore.SavedStatuses[1].Status);
        Assert.Single(runRepository.SavedRuns);
        Assert.Equal("Failed", runRepository.SavedRuns[0].Status);
        Assert.Equal(1, runRepository.SavedRuns[0].Conflicts);
        Assert.Single(runRepository.ReplacedEntries);
        Assert.Equal("conflicts", runRepository.ReplacedEntries[0].entries.Single().Bucket);
        Assert.Equal("LDAP bind failed.", runRepository.ReplacedEntries[0].entries.Single().Reason);
    }

    [Fact]
    public async Task ApplyAsync_PersistsFailureReportWhenExceptionMessageContainsNewlines()
    {
        var worker = new WorkerSnapshot(
            WorkerId: "10001",
            PreferredName: "Winnie",
            LastName: "Sample101",
            Department: "IT",
            TargetOu: "OU=LabUsers,DC=example,DC=com",
            IsPrehire: false,
            Attributes: new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase));

        var preview = new WorkerPreviewResult(
            ReportPath: null,
            RunId: "preview-10001-3",
            PreviousRunId: null,
            Fingerprint: "fingerprint-3",
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
            SamAccountName: "10001",
            ManagerDistinguishedName: null,
            TargetOu: worker.TargetOu,
            CurrentDistinguishedName: null,
            CurrentEnabled: null,
            ProposedEnable: true,
            OperationSummary: null,
            DiffRows:
            [
                new DiffRow("UserPrincipalName", "resolved email local-part", "(unset)", "preview.email@Exampleenergy.com", true)
            ],
            SourceAttributes: [],
            UsedSourceAttributes: [],
            UnusedSourceAttributes: [],
            MissingSourceAttributes: [],
            Entries: []);

        var runtimeStatusStore = new CapturingRuntimeStatusStore();
        var runRepository = new CapturingRunRepository(preview);
        var service = new ApplyPreviewService(
            new StubWorkerSource(worker),
            new DirectoryMutationCommandBuilder(),
            new ThrowingDirectoryCommandGateway(new InvalidOperationException("LDAP bind failed.\nServer said no.")),
            runRepository,
            runtimeStatusStore,
            new RealSyncSettings(),
            NullLogger<ApplyPreviewService>.Instance);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => service.ApplyAsync(
            new ApplyPreviewRequest(
                WorkerId: worker.WorkerId,
                PreviewRunId: preview.RunId!,
                PreviewFingerprint: preview.Fingerprint,
                AcknowledgeRealSync: true),
            CancellationToken.None));

        Assert.Equal("LDAP bind failed.\nServer said no.", exception.Message);
        Assert.Equal(2, runtimeStatusStore.SavedStatuses.Count);
        Assert.Single(runRepository.SavedRuns);
        Assert.Equal("Failed", runRepository.SavedRuns[0].Status);
        Assert.Single(runRepository.ReplacedEntries);
        Assert.Equal("LDAP bind failed.\nServer said no.", runRepository.ReplacedEntries[0].entries.Single().Reason);
    }

    [Fact]
    public async Task ApplyAsync_RequiresAcknowledgement()
    {
        var worker = new WorkerSnapshot(
            WorkerId: "10001",
            PreferredName: "Winnie",
            LastName: "Sample101",
            Department: "IT",
            TargetOu: "OU=LabUsers,DC=example,DC=com",
            IsPrehire: false,
            Attributes: new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase));

        var preview = new WorkerPreviewResult(
            ReportPath: null,
            RunId: "preview-10001-4",
            PreviousRunId: null,
            Fingerprint: "fingerprint-4",
            Mode: "Preview",
            Status: "Planned",
            ErrorMessage: null,
            ArtifactType: "WorkerPreview",
            SuccessFactorsAuth: "NativeScaffold",
            WorkerId: worker.WorkerId,
            Buckets: ["updates"],
            MatchedExistingUser: true,
            ReviewCategory: null,
            ReviewCaseType: null,
            Reason: null,
            OperatorActionSummary: null,
            SamAccountName: "10001",
            ManagerDistinguishedName: null,
            TargetOu: worker.TargetOu,
            CurrentDistinguishedName: "CN=Sample101\\, Winnie,OU=LabUsers,DC=example,DC=com",
            CurrentEnabled: true,
            ProposedEnable: true,
            OperationSummary: null,
            DiffRows:
            [
                new DiffRow("UserPrincipalName", "resolved email local-part", "(unset)", "preview.email@Exampleenergy.com", true)
            ],
            SourceAttributes: [],
            UsedSourceAttributes: [],
            UnusedSourceAttributes: [],
            MissingSourceAttributes: [],
            Entries: []);

        var service = new ApplyPreviewService(
            new StubWorkerSource(worker),
            new DirectoryMutationCommandBuilder(),
            new CapturingDirectoryCommandGateway(),
            new StubRunRepository(preview),
            new StubRuntimeStatusStore(),
            new RealSyncSettings(),
            NullLogger<ApplyPreviewService>.Instance);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => service.ApplyAsync(
            new ApplyPreviewRequest(
                WorkerId: worker.WorkerId,
                PreviewRunId: preview.RunId!,
                PreviewFingerprint: preview.Fingerprint,
                AcknowledgeRealSync: false),
            CancellationToken.None));

        Assert.Equal("Acknowledge the real AD sync before applying this preview.", exception.Message);
    }

    [Fact]
    public async Task ApplyAsync_RejectsPreviewWhenRequiredSourceAttributesAreMissing()
    {
        var worker = new WorkerSnapshot(
            WorkerId: "10001",
            PreferredName: "Winnie",
            LastName: "Sample101",
            Department: "IT",
            TargetOu: "OU=LabUsers,DC=example,DC=com",
            IsPrehire: false,
            Attributes: new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase));

        var preview = new WorkerPreviewResult(
            ReportPath: null,
            RunId: "preview-10001-missing-required",
            PreviousRunId: null,
            Fingerprint: "fingerprint-missing-required",
            Mode: "Preview",
            Status: "Planned",
            ErrorMessage: null,
            ArtifactType: "WorkerPreview",
            SuccessFactorsAuth: "NativeScaffold",
            WorkerId: worker.WorkerId,
            Buckets: ["unchanged"],
            MatchedExistingUser: false,
            ReviewCategory: null,
            ReviewCaseType: null,
            Reason: "Required mapping for employeeType has no value.",
            OperatorActionSummary: null,
            SamAccountName: "10001",
            ManagerDistinguishedName: null,
            TargetOu: worker.TargetOu,
            CurrentDistinguishedName: null,
            CurrentEnabled: null,
            ProposedEnable: true,
            OperationSummary: null,
            DiffRows:
            [
                new DiffRow("UserPrincipalName", "resolved email local-part", "(unset)", "preview.email@Exampleenergy.com", true)
            ],
            SourceAttributes: [],
            UsedSourceAttributes: [],
            UnusedSourceAttributes: [],
            MissingSourceAttributes:
            [
                new MissingSourceAttributeRow("employeeType", "Required mapping for employeeType has no value.")
            ],
            Entries: []);

        var directoryCommandGateway = new CapturingDirectoryCommandGateway();
        var service = new ApplyPreviewService(
            new StubWorkerSource(worker),
            new DirectoryMutationCommandBuilder(),
            directoryCommandGateway,
            new StubRunRepository(preview),
            new StubRuntimeStatusStore(),
            new RealSyncSettings(),
            NullLogger<ApplyPreviewService>.Instance);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => service.ApplyAsync(
            new ApplyPreviewRequest(
                WorkerId: worker.WorkerId,
                PreviewRunId: preview.RunId!,
                PreviewFingerprint: preview.Fingerprint,
                AcknowledgeRealSync: true),
            CancellationToken.None));

        Assert.Equal("Preview cannot be applied because required source attributes are missing.", exception.Message);
        Assert.Null(directoryCommandGateway.LastCommand);
    }

    [Fact]
    public async Task ApplyAsync_RejectsWhenRealSyncIsDisabled()
    {
        var worker = new WorkerSnapshot(
            WorkerId: "10001",
            PreferredName: "Winnie",
            LastName: "Sample101",
            Department: "IT",
            TargetOu: "OU=LabUsers,DC=example,DC=com",
            IsPrehire: false,
            Attributes: new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase));

        var preview = new WorkerPreviewResult(
            ReportPath: null,
            RunId: "preview-10001-disabled",
            PreviousRunId: null,
            Fingerprint: "fingerprint-disabled",
            Mode: "Preview",
            Status: "Planned",
            ErrorMessage: null,
            ArtifactType: "WorkerPreview",
            SuccessFactorsAuth: "NativeScaffold",
            WorkerId: worker.WorkerId,
            Buckets: ["updates"],
            MatchedExistingUser: true,
            ReviewCategory: null,
            ReviewCaseType: null,
            Reason: null,
            OperatorActionSummary: null,
            SamAccountName: "10001",
            ManagerDistinguishedName: null,
            TargetOu: worker.TargetOu,
            CurrentDistinguishedName: "CN=Sample101\\, Winnie,OU=LabUsers,DC=example,DC=com",
            CurrentEnabled: true,
            ProposedEnable: true,
            OperationSummary: null,
            DiffRows:
            [
                new DiffRow("UserPrincipalName", "resolved email local-part", "(unset)", "preview.email@Exampleenergy.com", true)
            ],
            SourceAttributes: [],
            UsedSourceAttributes: [],
            UnusedSourceAttributes: [],
            MissingSourceAttributes: [],
            Entries: []);

        var service = new ApplyPreviewService(
            new StubWorkerSource(worker),
            new DirectoryMutationCommandBuilder(),
            new CapturingDirectoryCommandGateway(),
            new StubRunRepository(preview),
            new StubRuntimeStatusStore(),
            new RealSyncSettings(Enabled: false),
            NullLogger<ApplyPreviewService>.Instance);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => service.ApplyAsync(
            new ApplyPreviewRequest(
                WorkerId: worker.WorkerId,
                PreviewRunId: preview.RunId!,
                PreviewFingerprint: preview.Fingerprint,
                AcknowledgeRealSync: true),
            CancellationToken.None));

        Assert.Equal("Real AD sync is disabled for this environment.", exception.Message);
    }

    [Fact]
    public async Task ApplyAsync_DoesNotBlockWhenManagerCannotBeResolved()
    {
        var worker = new WorkerSnapshot(
            WorkerId: "10001",
            PreferredName: "Winnie",
            LastName: "Sample101",
            Department: "IT",
            TargetOu: "OU=LabUsers,DC=example,DC=com",
            IsPrehire: false,
            Attributes: new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
            {
                ["managerId"] = "10004"
            });

        var preview = new WorkerPreviewResult(
            ReportPath: null,
            RunId: "preview-10001-manager-optional",
            PreviousRunId: null,
            Fingerprint: "fingerprint-manager-optional",
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
            SamAccountName: "10001",
            ManagerDistinguishedName: null,
            TargetOu: worker.TargetOu,
            CurrentDistinguishedName: null,
            CurrentEnabled: null,
            ProposedEnable: true,
            OperationSummary: null,
            DiffRows:
            [
                new DiffRow("UserPrincipalName", "resolved email local-part", "(unset)", "preview.email@Exampleenergy.com", true),
                new DiffRow("mail", "resolved email local-part", "(unset)", "preview.email@Exampleenergy.com", true)
            ],
            SourceAttributes: [],
            UsedSourceAttributes: [],
            UnusedSourceAttributes: [],
            MissingSourceAttributes: [],
            Entries:
            [
                new WorkerPreviewEntry(
                    Bucket: "creates",
                    Item: ParseJson("""{"managerRequired":true}"""))
            ]);

        var directoryCommandGateway = new CapturingDirectoryCommandGateway();
        var service = new ApplyPreviewService(
            new StubWorkerSource(worker),
            new DirectoryMutationCommandBuilder(),
            directoryCommandGateway,
            new StubRunRepository(preview),
            new StubRuntimeStatusStore(),
            new RealSyncSettings(),
            NullLogger<ApplyPreviewService>.Instance);

        await service.ApplyAsync(
            new ApplyPreviewRequest(
                WorkerId: worker.WorkerId,
                PreviewRunId: preview.RunId!,
                PreviewFingerprint: preview.Fingerprint,
                AcknowledgeRealSync: true),
            CancellationToken.None);

        var command = Assert.IsType<DirectoryMutationCommand>(directoryCommandGateway.LastCommand);
        Assert.Equal("10004", command.ManagerId);
        Assert.Null(command.ManagerDistinguishedName);
    }

    private sealed class StubWorkerSource(WorkerSnapshot worker) : IWorkerSource
    {
        public Task<WorkerSnapshot?> GetWorkerAsync(string workerId, CancellationToken cancellationToken)
        {
            _ = workerId;
            _ = cancellationToken;
            return Task.FromResult<WorkerSnapshot?>(worker);
        }

        public async IAsyncEnumerable<WorkerSnapshot> ListWorkersAsync(WorkerListingMode mode, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
        {
            _ = mode;
            cancellationToken.ThrowIfCancellationRequested();
            yield return worker;
            await Task.Yield();
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

    private sealed class StubRunRepository(WorkerPreviewResult preview) : IRunRepository
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

        public Task<WorkerPreviewResult?> GetWorkerPreviewAsync(string runId, CancellationToken cancellationToken)
        {
            _ = runId;
            _ = cancellationToken;
            return Task.FromResult<WorkerPreviewResult?>(preview);
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

        public Task AppendRunEntryAsync(RunEntryRecord entry, CancellationToken cancellationToken)
        {
            _ = entry;
            _ = cancellationToken;
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<RunEntry>> GetRunEntriesAsync(string runId, string? bucket, string? workerId, string? reason, string? filter, string? employmentStatus, string? entryId, int skip, int take, CancellationToken cancellationToken)
        {
            _ = runId;
            _ = bucket;
            _ = workerId;
            _ = reason;
            _ = filter;
            _ = employmentStatus;
            _ = entryId;
            _ = skip;
            _ = take;
            _ = cancellationToken;
            return Task.FromResult<IReadOnlyList<RunEntry>>([]);
        }

        public Task<int> CountRunEntriesAsync(string runId, string? bucket, string? workerId, string? reason, string? filter, string? employmentStatus, string? entryId, CancellationToken cancellationToken)
        {
            _ = runId;
            _ = bucket;
            _ = workerId;
            _ = reason;
            _ = filter;
            _ = employmentStatus;
            _ = entryId;
            _ = cancellationToken;
            return Task.FromResult(0);
        }

        public Task<IReadOnlyList<ChangedAttributeTotal>> GetRunEntryAttributeTotalsAsync(string runId, string? bucket, string? workerId, string? reason, string? filter, string? employmentStatus, string? entryId, CancellationToken cancellationToken)
        {
            _ = runId;
            _ = bucket;
            _ = workerId;
            _ = reason;
            _ = filter;
            _ = employmentStatus;
            _ = entryId;
            _ = cancellationToken;
            return Task.FromResult<IReadOnlyList<ChangedAttributeTotal>>([]);
        }
    }

    private sealed class CapturingRunRepository(WorkerPreviewResult preview) : IRunRepository
    {
        public List<RunRecord> SavedRuns { get; } = [];
        public List<(string runId, IReadOnlyList<RunEntryRecord> entries)> ReplacedEntries { get; } = [];

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

        public Task<WorkerPreviewResult?> GetWorkerPreviewAsync(string runId, CancellationToken cancellationToken)
        {
            _ = runId;
            _ = cancellationToken;
            return Task.FromResult<WorkerPreviewResult?>(preview);
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
            _ = cancellationToken;
            ReplacedEntries.Add((runId, entries));
            return Task.CompletedTask;
        }

        public Task AppendRunEntryAsync(RunEntryRecord entry, CancellationToken cancellationToken)
        {
            _ = cancellationToken;
            ReplacedEntries.Add((entry.RunId, [entry]));
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<RunEntry>> GetRunEntriesAsync(string runId, string? bucket, string? workerId, string? reason, string? filter, string? employmentStatus, string? entryId, int skip, int take, CancellationToken cancellationToken)
        {
            _ = runId;
            _ = bucket;
            _ = workerId;
            _ = reason;
            _ = filter;
            _ = employmentStatus;
            _ = entryId;
            _ = skip;
            _ = take;
            _ = cancellationToken;
            return Task.FromResult<IReadOnlyList<RunEntry>>([]);
        }

        public Task<int> CountRunEntriesAsync(string runId, string? bucket, string? workerId, string? reason, string? filter, string? employmentStatus, string? entryId, CancellationToken cancellationToken)
        {
            _ = runId;
            _ = bucket;
            _ = workerId;
            _ = reason;
            _ = filter;
            _ = employmentStatus;
            _ = entryId;
            _ = cancellationToken;
            return Task.FromResult(0);
        }

        public Task<IReadOnlyList<ChangedAttributeTotal>> GetRunEntryAttributeTotalsAsync(string runId, string? bucket, string? workerId, string? reason, string? filter, string? employmentStatus, string? entryId, CancellationToken cancellationToken)
        {
            _ = runId;
            _ = bucket;
            _ = workerId;
            _ = reason;
            _ = filter;
            _ = employmentStatus;
            _ = entryId;
            _ = cancellationToken;
            return Task.FromResult<IReadOnlyList<ChangedAttributeTotal>>([]);
        }
    }

    private sealed class StubRuntimeStatusStore : IRuntimeStatusStore
    {
        public Task<RuntimeStatus?> GetCurrentAsync(CancellationToken cancellationToken)
        {
            _ = cancellationToken;
            return Task.FromResult<RuntimeStatus?>(null);
        }

        public Task<bool> TryStartAsync(RuntimeStatus status, CancellationToken cancellationToken)
        {
            _ = status;
            _ = cancellationToken;
            return Task.FromResult(true);
        }

        public Task SaveAsync(RuntimeStatus status, CancellationToken cancellationToken)
        {
            _ = status;
            _ = cancellationToken;
            return Task.CompletedTask;
        }
    }

    private sealed class CapturingRuntimeStatusStore : IRuntimeStatusStore
    {
        public List<RuntimeStatus> SavedStatuses { get; } = [];

        public Task<RuntimeStatus?> GetCurrentAsync(CancellationToken cancellationToken)
        {
            _ = cancellationToken;
            return Task.FromResult<RuntimeStatus?>(null);
        }

        public Task<bool> TryStartAsync(RuntimeStatus status, CancellationToken cancellationToken)
        {
            _ = cancellationToken;
            SavedStatuses.Add(status);
            return Task.FromResult(true);
        }

        public Task SaveAsync(RuntimeStatus status, CancellationToken cancellationToken)
        {
            _ = cancellationToken;
            SavedStatuses.Add(status);
            return Task.CompletedTask;
        }
    }

    private static System.Text.Json.JsonElement ParseJson(string json)
    {
        using var document = System.Text.Json.JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }
}

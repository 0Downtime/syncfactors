using SyncFactors.Contracts;
using SyncFactors.Domain;

namespace SyncFactors.Domain.Tests;

public sealed class PreviewApplyFreshnessValidatorTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-07-14T12:00:00Z");

    [Fact]
    public async Task ValidateAsync_RejectsPreviewWhenSourceStateChangedAfterReview()
    {
        var reviewedWorker = CreateWorker(department: "IT");
        var currentWorker = CreateWorker(department: "Finance");
        var directoryUser = CreateDirectoryUser(enabled: true);
        var preview = CreatePreview(reviewedWorker, directoryUser, Now);
        var validator = new PreviewApplyFreshnessValidator(
            new StubWorkerSource(currentWorker),
            new StubWorkerPlanningService(directoryUser),
            new FakeTimeProvider(Now));

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            validator.ValidateAsync(preview, CancellationToken.None));

        Assert.Equal("The saved preview no longer matches the current source state. Refresh preview before applying.", exception.Message);
    }

    [Fact]
    public async Task ValidateAsync_RejectsPreviewWhenDirectoryStateChangedAfterReview()
    {
        var worker = CreateWorker(department: "IT");
        var reviewedDirectoryUser = CreateDirectoryUser(enabled: true);
        var currentDirectoryUser = CreateDirectoryUser(enabled: false);
        var preview = CreatePreview(worker, reviewedDirectoryUser, Now);
        var validator = new PreviewApplyFreshnessValidator(
            new StubWorkerSource(worker),
            new StubWorkerPlanningService(currentDirectoryUser),
            new FakeTimeProvider(Now));

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            validator.ValidateAsync(preview, CancellationToken.None));

        Assert.Equal("The saved preview no longer matches the current Active Directory state. Refresh preview before applying.", exception.Message);
    }

    [Fact]
    public async Task ValidateAsync_RejectsExpiredPreviewBeforeMutation()
    {
        var worker = CreateWorker(department: "IT");
        var directoryUser = CreateDirectoryUser(enabled: true);
        var preview = CreatePreview(worker, directoryUser, Now.AddMinutes(-16));
        var validator = new PreviewApplyFreshnessValidator(
            new StubWorkerSource(worker),
            new StubWorkerPlanningService(directoryUser),
            new FakeTimeProvider(Now));

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            validator.ValidateAsync(preview, CancellationToken.None));

        Assert.Equal("The saved preview has expired. Refresh preview before applying.", exception.Message);
    }

    private static WorkerSnapshot CreateWorker(string department) =>
        new(
            WorkerId: "10001",
            PreferredName: "Winnie",
            LastName: "Sample",
            Department: department,
            TargetOu: "OU=LabUsers,DC=example,DC=com",
            IsPrehire: false,
            Attributes: new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
            {
                ["department"] = department,
                ["managerId"] = "90001"
            });

    private static DirectoryUserSnapshot CreateDirectoryUser(bool enabled) =>
        new(
            SamAccountName: "10001",
            DistinguishedName: "CN=Sample,OU=LabUsers,DC=example,DC=com",
            Enabled: enabled,
            DisplayName: "Winnie Sample",
            Attributes: new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
            {
                ["department"] = "IT"
            });

    private static WorkerPreviewResult CreatePreview(
        WorkerSnapshot worker,
        DirectoryUserSnapshot directoryUser,
        DateTimeOffset createdAtUtc) =>
        new(
            ReportPath: null,
            RunId: "preview-10001",
            PreviousRunId: null,
            Fingerprint: "fingerprint",
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
            CurrentDistinguishedName: directoryUser.DistinguishedName,
            CurrentEnabled: directoryUser.Enabled,
            ProposedEnable: true,
            OperationSummary: null,
            DiffRows: [new DiffRow("UserPrincipalName", "email", "old@example.test", "new@example.test", true)],
            SourceAttributes: [],
            UsedSourceAttributes: [],
            UnusedSourceAttributes: [],
            MissingSourceAttributes: [],
            Entries: [],
            CreatedAtUtc: createdAtUtc,
            SourceStateFingerprint: WorkerPreviewStateFingerprint.ComputeSource(worker),
            DirectoryStateFingerprint: WorkerPreviewStateFingerprint.ComputeDirectory(directoryUser));

    private sealed class StubWorkerSource(WorkerSnapshot worker) : IWorkerSource
    {
        public Task<WorkerSnapshot?> GetWorkerAsync(string workerId, CancellationToken cancellationToken) =>
            Task.FromResult<WorkerSnapshot?>(string.Equals(workerId, worker.WorkerId, StringComparison.Ordinal) ? worker : null);

        public async IAsyncEnumerable<WorkerSnapshot> ListWorkersAsync(WorkerListingMode mode, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
        {
            _ = mode;
            cancellationToken.ThrowIfCancellationRequested();
            yield return worker;
            await Task.CompletedTask;
        }
    }

    private sealed class StubWorkerPlanningService(DirectoryUserSnapshot directoryUser) : IWorkerPlanningService
    {
        public Task<PlannedWorkerAction> PlanAsync(WorkerSnapshot worker, string? logPath, CancellationToken cancellationToken)
        {
            _ = logPath;
            _ = cancellationToken;
            return Task.FromResult(new PlannedWorkerAction(
                Worker: worker,
                DirectoryUser: directoryUser,
                Identity: new IdentityMatchResult("updates", true, worker.WorkerId, null, null),
                ManagerDistinguishedName: null,
                ProposedEmailAddress: "new@example.test",
                AttributeChanges: [],
                MissingSourceAttributes: [],
                Bucket: "updates",
                CurrentOu: worker.TargetOu,
                TargetOu: worker.TargetOu,
                CurrentEnabled: directoryUser.Enabled,
                TargetEnabled: true,
                PrimaryAction: "UpdateUser",
                Operations: [new DirectoryOperation("UpdateUser")],
                ReviewCategory: null,
                ReviewCaseType: null,
                Reason: null,
                CanAutoApply: true));
        }
    }

    private sealed class FakeTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}

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
    public async Task TryExecuteAsync_RechecksEligibilityImmediatelyBeforeDelete()
    {
        var retentionStore = new SequencedGraveyardRetentionStore(
            [CreateRecord("10001", isOnHold: false, endDateUtc: DateTimeOffset.Parse("2026-02-01T00:00:00Z"))],
            []);
        var commandGateway = new CapturingDirectoryCommandGateway();
        var lifecycle = new CapturingRunLifecycleService();
        var coordinator = CreateCoordinator(
            retentionStore,
            CreateDirectoryGateway("10001"),
            commandGateway,
            lifecycle,
            autoDeleteEnabled: true,
            now: DateTimeOffset.Parse("2026-04-11T12:00:00Z"));

        var runId = await coordinator.TryExecuteAsync(CancellationToken.None);

        Assert.NotNull(runId);
        Assert.Empty(commandGateway.Commands);
        var entry = Assert.Single(lifecycle.Entries);
        Assert.Equal("conflicts", entry.Bucket);
        Assert.Equal("DeletePreconditionFailed", entry.ReviewCaseType);
        Assert.False(entry.Item.GetProperty("applied").GetBoolean());
    }

    [Fact]
    public async Task TryExecuteAsync_DoesNotDeleteWhenHoldIsPlacedAfterTheInitialSnapshot()
    {
        var retentionStore = new StubGraveyardRetentionStore(
            [CreateRecord("10001", isOnHold: false, endDateUtc: DateTimeOffset.Parse("2026-02-01T00:00:00Z"))],
            placeHoldAfterClaim: true);
        var commandGateway = new CapturingDirectoryCommandGateway();
        var lifecycle = new CapturingRunLifecycleService();
        var coordinator = CreateCoordinator(retentionStore, CreateDirectoryGateway("10001"), commandGateway, lifecycle, true, DateTimeOffset.Parse("2026-04-11T12:00:00Z"));

        await coordinator.TryExecuteAsync(CancellationToken.None);

        Assert.Empty(commandGateway.Commands);
        var entry = Assert.Single(lifecycle.Entries);
        Assert.Equal("conflicts", entry.Bucket);
        Assert.Equal("DeletePreconditionFailed", entry.ReviewCaseType);
    }

    [Fact]
    public async Task TryExecuteAsync_RejectsHoldBetweenFinalRevalidationAndDirectoryMutation_WhenDeletionLeaseIsActive()
    {
        var retentionStore = new BarrierControlledGraveyardRetentionStore(
            CreateRecord("10001", isOnHold: false, endDateUtc: DateTimeOffset.Parse("2026-02-01T00:00:00Z")));
        var commandGateway = new BarrierControlledDirectoryCommandGateway();
        var coordinator = CreateCoordinator(
            retentionStore,
            CreateDirectoryGateway("10001"),
            commandGateway,
            new CapturingRunLifecycleService(),
            autoDeleteEnabled: true,
            now: DateTimeOffset.Parse("2026-04-11T12:00:00Z"));

        var execution = coordinator.TryExecuteAsync(CancellationToken.None);
        await commandGateway.WaitForMutationAttemptAsync();

        var holdResult = await retentionStore.SetHoldAsync(
            "10001",
            isOnHold: true,
            actingUserId: "admin-1",
            changedAtUtc: DateTimeOffset.Parse("2026-04-11T12:00:00Z"),
            CancellationToken.None);
        commandGateway.ReleaseMutation();
        await execution;

        Assert.False(holdResult.Succeeded);
        Assert.Equal(GraveyardHoldChangeOutcome.ActiveDeletionLease, holdResult.Outcome);
        Assert.False(retentionStore.HoldWasAccepted);
        Assert.Single(commandGateway.CompletedCommands);
    }

    [Fact]
    public async Task TryExecuteAsync_ReleasesDeletionLeaseWhenDirectoryMutationFails()
    {
        var retentionStore = new StubGraveyardRetentionStore(
            [CreateRecord("10001", isOnHold: false, endDateUtc: DateTimeOffset.Parse("2026-02-01T00:00:00Z"))]);
        var lifecycle = new CapturingRunLifecycleService();
        var coordinator = CreateCoordinator(
            retentionStore,
            CreateDirectoryGateway("10001"),
            new FailingDirectoryCommandGateway(),
            lifecycle,
            autoDeleteEnabled: true,
            now: DateTimeOffset.Parse("2026-04-11T12:00:00Z"));

        await coordinator.TryExecuteAsync(CancellationToken.None);

        Assert.Equal(0, retentionStore.ActiveClaimCount);
        Assert.Empty(retentionStore.ResolvedWorkerIds);
        var entry = Assert.Single(lifecycle.Entries);
        Assert.Equal("conflicts", entry.Bucket);
        Assert.True(entry.Item.GetProperty("applied").GetBoolean());
        Assert.False(entry.Item.GetProperty("succeeded").GetBoolean());
    }

    [Fact]
    public async Task TryExecuteAsync_ManualReviewDeletions_RebucketsDeleteWithoutExecutingDirectoryMutation()
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
            autoDeleteEnabled: true,
            now: DateTimeOffset.Parse("2026-04-11T12:00:00Z"),
            manualReviewDeletions: true);

        var runId = await coordinator.TryExecuteAsync(CancellationToken.None);

        Assert.NotNull(runId);
        Assert.Empty(commandGateway.Commands);
        Assert.Empty(retentionStore.ResolvedWorkerIds);
        Assert.Equal(1, lifecycle.CompletedCalls);
        var entry = Assert.Single(lifecycle.Entries);
        Assert.Equal("manualReview", entry.Bucket);
        Assert.Equal("SafetyPolicy", entry.ReviewCategory);
        Assert.Equal("DeletionRequiresManualReview", entry.ReviewCaseType);
        Assert.Equal(JsonValueKind.Null, entry.Item.GetProperty("action").ValueKind);
        Assert.False(entry.Item.GetProperty("applied").GetBoolean());
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
    public async Task ApproveDeleteAsync_QueuesEligibleUserForSerializedExecution_WhenAutoDeleteIsDisabled()
    {
        var retentionStore = new StubGraveyardRetentionStore(
            [
                CreateRecord("10001", isOnHold: false, endDateUtc: DateTimeOffset.Parse("2026-02-01T00:00:00Z"))
            ]);
        var commandGateway = new CapturingDirectoryCommandGateway();
        var lifecycle = new CapturingRunLifecycleService();
        var runQueueStore = new CapturingRunQueueStore();
        var coordinator = CreateCoordinator(
            retentionStore,
            CreateDirectoryGateway("10001"),
            commandGateway,
            lifecycle,
            autoDeleteEnabled: false,
            now: DateTimeOffset.Parse("2026-04-11T12:00:00Z"),
            runQueueStore: runQueueStore);

        var result = await coordinator.ApproveDeleteAsync("10001", "admin", CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.Equal(runQueueStore.Enqueued!.RequestId, result.RunId);
        Assert.Equal("GraveyardDeleteApproval", runQueueStore.Enqueued.Mode);
        Assert.Equal(RunQueueProtocol.AuthenticatedAdminDeletionQueueTrigger, runQueueStore.Enqueued.RunTrigger);
        Assert.Equal("10001", runQueueStore.Enqueued.TargetWorkerId);
        Assert.Empty(commandGateway.Commands);

        var executedRunId = await coordinator.ExecuteApprovedDeleteAsync(runQueueStore.Enqueued, CancellationToken.None);

        Assert.StartsWith("graveyard-delete-approval-", executedRunId, StringComparison.Ordinal);
        Assert.Single(commandGateway.Commands);
        Assert.Equal(["10001"], retentionStore.ResolvedWorkerIds);
        Assert.Equal(1, lifecycle.CompletedCalls);
        Assert.Contains(lifecycle.Entries, entry => entry.WorkerId == "10001" && entry.Bucket == "deletions");
    }

    [Fact]
    public async Task ExecuteApprovedDeleteAsync_RejectsForgedApprovalMetadataBeforeDeletionCoordination()
    {
        var retentionStore = new StubGraveyardRetentionStore(
            [CreateRecord("10001", isOnHold: false, endDateUtc: DateTimeOffset.Parse("2026-02-01T00:00:00Z"))]);
        var commandGateway = new CapturingDirectoryCommandGateway();
        var lifecycle = new CapturingRunLifecycleService();
        var coordinator = CreateCoordinator(
            retentionStore,
            CreateDirectoryGateway("10001"),
            commandGateway,
            lifecycle,
            autoDeleteEnabled: false,
            now: DateTimeOffset.Parse("2026-04-11T12:00:00Z"));
        var forgedRequest = new RunQueueRequest(
            RequestId: "forged-approval",
            Mode: RunQueueProtocol.GraveyardDeleteApprovalMode,
            DryRun: false,
            RunTrigger: "AdminApproval",
            RequestedBy: "admin",
            Status: "InProgress",
            RequestedAt: DateTimeOffset.Parse("2026-04-11T11:59:00Z"),
            StartedAt: DateTimeOffset.Parse("2026-04-11T12:00:00Z"),
            CompletedAt: null,
            RunId: null,
            ErrorMessage: null,
            TargetWorkerId: "10001");

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            coordinator.ExecuteApprovedDeleteAsync(forgedRequest, CancellationToken.None));

        Assert.Contains("provenance", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(commandGateway.Commands);
        Assert.Empty(retentionStore.ResolvedWorkerIds);
        Assert.Empty(lifecycle.Entries);
    }

    [Fact]
    public async Task ApproveDeleteAsync_RejectsDuplicateApprovalWhileTheFirstApprovalIsQueued()
    {
        var retentionStore = new StubGraveyardRetentionStore(
            [CreateRecord("10001", isOnHold: false, endDateUtc: DateTimeOffset.Parse("2026-02-01T00:00:00Z"))]);
        var commandGateway = new CapturingDirectoryCommandGateway();
        var coordinator = CreateCoordinator(
            retentionStore,
            CreateDirectoryGateway("10001"),
            commandGateway,
            new CapturingRunLifecycleService(),
            autoDeleteEnabled: false,
            now: DateTimeOffset.Parse("2026-04-11T12:00:00Z"),
            runQueueStore: new CapturingRunQueueStore());

        var first = await coordinator.ApproveDeleteAsync("10001", "admin", CancellationToken.None);
        var duplicate = await coordinator.ApproveDeleteAsync("10001", "admin", CancellationToken.None);

        Assert.True(first.Succeeded);
        Assert.False(duplicate.Succeeded);
        Assert.Contains("pending or in progress", duplicate.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(commandGateway.Commands);
    }

    [Theory]
    [InlineData(false, false, "Real AD sync is disabled for this environment.")]
    [InlineData(true, true, "Dry-run-only mode is enabled. Live AD writes are disabled for this environment.")]
    public async Task ApproveDeleteAsync_RejectsDirectoryMutation_WhenLiveWritesAreDisabled(
        bool enabled,
        bool dryRunOnly,
        string expectedMessage)
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
            now: DateTimeOffset.Parse("2026-04-11T12:00:00Z"),
            realSyncSettings: new RealSyncSettings(enabled, dryRunOnly));

        var result = await coordinator.ApproveDeleteAsync("10001", "admin", CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Equal(expectedMessage, result.Message);
        Assert.Empty(commandGateway.Commands);
        Assert.Empty(retentionStore.ResolvedWorkerIds);
    }

    [Theory]
    [InlineData(false, false, "Real AD sync is disabled for this environment.")]
    [InlineData(true, true, "Dry-run-only mode is enabled. Live AD writes are disabled for this environment.")]
    public async Task TryExecuteAsync_RejectsDirectoryMutation_WhenLiveWritesAreDisabled(
        bool enabled,
        bool dryRunOnly,
        string expectedMessage)
    {
        var retentionStore = new StubGraveyardRetentionStore(
            [
                CreateRecord("10001", isOnHold: false, endDateUtc: DateTimeOffset.Parse("2026-02-01T00:00:00Z"))
            ]);
        var commandGateway = new CapturingDirectoryCommandGateway();
        var coordinator = CreateCoordinator(
            retentionStore,
            CreateDirectoryGateway("10001"),
            commandGateway,
            new CapturingRunLifecycleService(),
            autoDeleteEnabled: true,
            now: DateTimeOffset.Parse("2026-04-11T12:00:00Z"),
            realSyncSettings: new RealSyncSettings(enabled, dryRunOnly));

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => coordinator.TryExecuteAsync(CancellationToken.None));

        Assert.Equal(expectedMessage, exception.Message);
        Assert.Empty(commandGateway.Commands);
        Assert.Empty(retentionStore.ResolvedWorkerIds);
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
        IGraveyardRetentionStore retentionStore,
        IDirectoryGateway directoryGateway,
        IDirectoryCommandGateway commandGateway,
        CapturingRunLifecycleService lifecycle,
        bool autoDeleteEnabled,
        DateTimeOffset now,
        int maxDeletionsPerRun = 10,
        bool manualReviewDeletions = false,
        RealSyncSettings? realSyncSettings = null,
        CapturingRunQueueStore? runQueueStore = null)
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
            runQueueStore ?? new CapturingRunQueueStore(),
            new GraveyardDeletionQueueSettings(RetentionDays: 30, AutoDeleteEnabled: autoDeleteEnabled),
            new WorkerRunSettings(
                MaxCreatesPerRun: 10,
                MaxDisablesPerRun: 10,
                MaxDeletionsPerRun: maxDeletionsPerRun,
                ManualReviewDeletions: manualReviewDeletions),
            realSyncSettings ?? new RealSyncSettings(),
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

    private sealed class BarrierControlledGraveyardRetentionStore(GraveyardRetentionRecord initialRecord) : IGraveyardRetentionStore
    {
        private GraveyardRetentionRecord _record = initialRecord;
        private GraveyardDeletionClaim? _claim;

        public bool HoldWasAccepted { get; private set; }

        public Task UpsertObservedAsync(GraveyardRetentionRecord record, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task ResolveAsync(string workerId, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task<IReadOnlyList<GraveyardRetentionRecord>> ListActiveAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<GraveyardRetentionRecord>>([_record]);

        public Task<GraveyardHoldChangeResult> SetHoldAsync(string workerId, bool isOnHold, string? actingUserId, DateTimeOffset changedAtUtc, CancellationToken cancellationToken)
        {
            if (_claim is { } claim && claim.LeaseExpiresAtUtc > changedAtUtc)
            {
                return Task.FromResult(new GraveyardHoldChangeResult(GraveyardHoldChangeOutcome.ActiveDeletionLease));
            }

            HoldWasAccepted = true;
            _record = _record with
            {
                IsOnHold = isOnHold,
                HoldPlacedAtUtc = isOnHold ? changedAtUtc : null,
                HoldPlacedBy = isOnHold ? actingUserId : null,
                Version = _record.Version + 1,
                DeletionClaimId = null,
                DeletionClaimVersion = null
            };
            _claim = null;
            return Task.FromResult(new GraveyardHoldChangeResult(GraveyardHoldChangeOutcome.Accepted));
        }

        public Task<GraveyardDeletionClaim?> TryClaimDeletionAsync(string workerId, long expectedVersion, string claimId, DateTimeOffset now, DateTimeOffset leaseExpiresAtUtc, CancellationToken cancellationToken)
        {
            if (!string.Equals(workerId, _record.WorkerId, StringComparison.OrdinalIgnoreCase) ||
                _record.Version != expectedVersion ||
                _record.IsOnHold ||
                _claim is { LeaseExpiresAtUtc: var currentLeaseExpiresAtUtc } && currentLeaseExpiresAtUtc > now)
            {
                return Task.FromResult<GraveyardDeletionClaim?>(null);
            }

            _claim = new GraveyardDeletionClaim(workerId, claimId, _record.Version + 1, leaseExpiresAtUtc);
            _record = _record with
            {
                Version = _claim.Version,
                DeletionClaimId = _claim.ClaimId,
                DeletionClaimVersion = _claim.Version
            };
            return Task.FromResult<GraveyardDeletionClaim?>(_claim);
        }

        public Task<GraveyardDeletionClaim?> GetDeletionClaimAsync(string workerId, string claimId, long claimVersion, DateTimeOffset now, CancellationToken cancellationToken) =>
            Task.FromResult(_claim is { } claim &&
                            string.Equals(workerId, claim.WorkerId, StringComparison.OrdinalIgnoreCase) &&
                            string.Equals(claimId, claim.ClaimId, StringComparison.Ordinal) &&
                            claimVersion == claim.Version &&
                            claim.LeaseExpiresAtUtc > now
                ? claim
                : null);

        public Task<bool> ReleaseDeletionClaimAsync(string workerId, string claimId, long claimVersion, CancellationToken cancellationToken) =>
            Task.FromResult(RemoveClaim(workerId, claimId, claimVersion));

        public Task<bool> ResolveDeletionClaimAsync(string workerId, string claimId, long claimVersion, CancellationToken cancellationToken) =>
            Task.FromResult(RemoveClaim(workerId, claimId, claimVersion));

        public Task<GraveyardRetentionReportStatus> GetReportStatusAsync(CancellationToken cancellationToken) =>
            Task.FromResult(new GraveyardRetentionReportStatus(null, null, null));

        public Task RecordReportAttemptAsync(DateTimeOffset attemptedAt, string? error, DateTimeOffset? sentAtUtc, CancellationToken cancellationToken) =>
            Task.CompletedTask;

        private bool RemoveClaim(string workerId, string claimId, long claimVersion)
        {
            if (_claim is not { } claim ||
                !string.Equals(workerId, claim.WorkerId, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(claimId, claim.ClaimId, StringComparison.Ordinal) ||
                claimVersion != claim.Version)
            {
                return false;
            }

            _claim = null;
            _record = _record with
            {
                Version = _record.Version + 1,
                DeletionClaimId = null,
                DeletionClaimVersion = null
            };
            return true;
        }
    }

    private sealed class BarrierControlledDirectoryCommandGateway : IDirectoryCommandGateway
    {
        private readonly TaskCompletionSource _mutationAttempted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _allowMutation = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public List<DirectoryMutationCommand> CompletedCommands { get; } = [];

        public async Task<DirectoryCommandResult> ExecuteAsync(DirectoryMutationCommand command, CancellationToken cancellationToken)
        {
            _mutationAttempted.TrySetResult();
            await _allowMutation.Task.WaitAsync(cancellationToken);
            CompletedCommands.Add(command);
            return new DirectoryCommandResult(true, command.Action, command.SamAccountName, command.CurrentDistinguishedName, "Deleted", null);
        }

        public Task WaitForMutationAttemptAsync() => _mutationAttempted.Task;

        public void ReleaseMutation() => _allowMutation.TrySetResult();
    }

    private sealed class StubGraveyardRetentionStore(
        IReadOnlyList<GraveyardRetentionRecord> records,
        bool placeHoldAfterClaim = false) : IGraveyardRetentionStore
    {
        public List<string> ResolvedWorkerIds { get; } = [];
        private readonly Dictionary<string, GraveyardDeletionClaim> _claims = new(StringComparer.OrdinalIgnoreCase);
        private bool _holdPlacedAfterClaim;

        public int ActiveClaimCount => _claims.Count;

        public Task UpsertObservedAsync(GraveyardRetentionRecord record, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task ResolveAsync(string workerId, CancellationToken cancellationToken)
        {
            ResolvedWorkerIds.Add(workerId);
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<GraveyardRetentionRecord>> ListActiveAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<GraveyardRetentionRecord>>(
                records.Select(record =>
                    _claims.TryGetValue(record.WorkerId, out var claim)
                        ? record with
                        {
                            Version = claim.Version,
                            IsOnHold = _holdPlacedAfterClaim,
                            HoldPlacedAtUtc = _holdPlacedAfterClaim ? DateTimeOffset.Parse("2026-04-11T12:00:00Z") : null,
                            HoldPlacedBy = _holdPlacedAfterClaim ? "admin-1" : null,
                            DeletionClaimId = claim.ClaimId,
                            DeletionClaimVersion = claim.Version
                        }
                        : record).ToArray());

        public Task<GraveyardHoldChangeResult> SetHoldAsync(string workerId, bool isOnHold, string? actingUserId, DateTimeOffset changedAtUtc, CancellationToken cancellationToken) =>
            Task.FromResult(new GraveyardHoldChangeResult(GraveyardHoldChangeOutcome.Accepted));

        public Task<GraveyardDeletionClaim?> TryClaimDeletionAsync(string workerId, long expectedVersion, string claimId, DateTimeOffset now, DateTimeOffset leaseExpiresAtUtc, CancellationToken cancellationToken)
        {
            var record = records.SingleOrDefault(candidate => string.Equals(candidate.WorkerId, workerId, StringComparison.OrdinalIgnoreCase));
            if (record is null || record.Version != expectedVersion || record.IsOnHold || _claims.ContainsKey(workerId))
            {
                return Task.FromResult<GraveyardDeletionClaim?>(null);
            }

            var claim = new GraveyardDeletionClaim(workerId, claimId, expectedVersion + 1, leaseExpiresAtUtc);
            _claims[workerId] = claim;
            _holdPlacedAfterClaim = placeHoldAfterClaim;
            return Task.FromResult<GraveyardDeletionClaim?>(claim);
        }

        public Task<GraveyardDeletionClaim?> GetDeletionClaimAsync(string workerId, string claimId, long claimVersion, DateTimeOffset now, CancellationToken cancellationToken) =>
            Task.FromResult(_claims.TryGetValue(workerId, out var claim) && claim.ClaimId == claimId && claim.Version == claimVersion && claim.LeaseExpiresAtUtc > now ? claim : null);

        public Task<bool> ReleaseDeletionClaimAsync(string workerId, string claimId, long claimVersion, CancellationToken cancellationToken) =>
            Task.FromResult(RemoveClaim(workerId, claimId, claimVersion));

        public Task<bool> ResolveDeletionClaimAsync(string workerId, string claimId, long claimVersion, CancellationToken cancellationToken)
        {
            if (!RemoveClaim(workerId, claimId, claimVersion))
            {
                return Task.FromResult(false);
            }

            ResolvedWorkerIds.Add(workerId);
            return Task.FromResult(true);
        }

        private bool RemoveClaim(string workerId, string claimId, long claimVersion)
        {
            if (!_claims.TryGetValue(workerId, out var claim) || claim.ClaimId != claimId || claim.Version != claimVersion)
            {
                return false;
            }

            _claims.Remove(workerId);
            return true;
        }

        public Task<GraveyardRetentionReportStatus> GetReportStatusAsync(CancellationToken cancellationToken) =>
            Task.FromResult(new GraveyardRetentionReportStatus(null, null, null));

        public Task RecordReportAttemptAsync(DateTimeOffset attemptedAt, string? error, DateTimeOffset? sentAtUtc, CancellationToken cancellationToken) =>
            Task.CompletedTask;
    }

    private sealed class SequencedGraveyardRetentionStore(
        IReadOnlyList<GraveyardRetentionRecord> initialRecords,
        IReadOnlyList<GraveyardRetentionRecord> currentRecords) : IGraveyardRetentionStore
    {
        private int listCalls;

        public Task UpsertObservedAsync(GraveyardRetentionRecord record, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task ResolveAsync(string workerId, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task<IReadOnlyList<GraveyardRetentionRecord>> ListActiveAsync(CancellationToken cancellationToken)
        {
            _ = cancellationToken;
            return Task.FromResult(listCalls++ == 0 ? initialRecords : currentRecords);
        }

        public Task<GraveyardHoldChangeResult> SetHoldAsync(string workerId, bool isOnHold, string? actingUserId, DateTimeOffset changedAtUtc, CancellationToken cancellationToken) =>
            Task.FromResult(new GraveyardHoldChangeResult(GraveyardHoldChangeOutcome.Accepted));

        public Task<GraveyardDeletionClaim?> TryClaimDeletionAsync(string workerId, long expectedVersion, string claimId, DateTimeOffset now, DateTimeOffset leaseExpiresAtUtc, CancellationToken cancellationToken) =>
            Task.FromResult<GraveyardDeletionClaim?>(new GraveyardDeletionClaim(workerId, claimId, expectedVersion + 1, leaseExpiresAtUtc));

        public Task<GraveyardDeletionClaim?> GetDeletionClaimAsync(string workerId, string claimId, long claimVersion, DateTimeOffset now, CancellationToken cancellationToken) =>
            Task.FromResult<GraveyardDeletionClaim?>(new GraveyardDeletionClaim(workerId, claimId, claimVersion, now.AddMinutes(1)));

        public Task<bool> ReleaseDeletionClaimAsync(string workerId, string claimId, long claimVersion, CancellationToken cancellationToken) => Task.FromResult(true);

        public Task<bool> ResolveDeletionClaimAsync(string workerId, string claimId, long claimVersion, CancellationToken cancellationToken) => Task.FromResult(true);

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

    private sealed class FailingDirectoryCommandGateway : IDirectoryCommandGateway
    {
        public Task<DirectoryCommandResult> ExecuteAsync(DirectoryMutationCommand command, CancellationToken cancellationToken) =>
            Task.FromResult(new DirectoryCommandResult(false, command.Action, command.SamAccountName, command.CurrentDistinguishedName, "Directory deletion failed", null));
    }

    private sealed class CapturingRunQueueStore : IRunQueueStore
    {
        public RunQueueRequest? Enqueued { get; private set; }

        public Task<RunQueueRequest> EnqueueAsync(StartRunRequest request, CancellationToken cancellationToken)
        {
            if (Enqueued is not null)
            {
                throw new RunQueueConflictException();
            }

            Enqueued = new RunQueueRequest(
                RequestId: "approval-request-1",
                Mode: request.Mode,
                DryRun: request.DryRun,
                RunTrigger: request.RunTrigger,
                RequestedBy: request.RequestedBy,
                Status: "Pending",
                RequestedAt: DateTimeOffset.UtcNow,
                StartedAt: null,
                CompletedAt: null,
                RunId: null,
                ErrorMessage: null,
                TargetWorkerId: request.TargetWorkerId);
            return Task.FromResult(Enqueued);
        }

        public Task<RunQueueRequest?> ClaimNextPendingAsync(string workerName, CancellationToken cancellationToken) => Task.FromResult<RunQueueRequest?>(null);
        public Task<RunQueueRequest?> GetAsync(string requestId, CancellationToken cancellationToken) => Task.FromResult<RunQueueRequest?>(null);
        public Task<RunQueueRequest?> GetPendingOrActiveAsync(CancellationToken cancellationToken) => Task.FromResult<RunQueueRequest?>(Enqueued);
        public Task<bool> HasPendingOrActiveRunAsync(CancellationToken cancellationToken) => Task.FromResult(Enqueued is not null);
        public Task<bool> CancelPendingOrActiveAsync(string? requestedBy, CancellationToken cancellationToken) => Task.FromResult(false);
        public Task<bool> IsCancellationRequestedAsync(string requestId, CancellationToken cancellationToken) => Task.FromResult(false);
        public Task CompleteAsync(string requestId, string runId, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task CancelAsync(string requestId, string? runId, string? errorMessage, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task FailAsync(string requestId, string? runId, string errorMessage, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task<int> RecoverOrphanedActiveRunsAsync(string? errorMessage, CancellationToken cancellationToken) => Task.FromResult(0);
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

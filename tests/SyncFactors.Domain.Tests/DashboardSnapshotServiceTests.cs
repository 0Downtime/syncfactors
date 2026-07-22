using SyncFactors.Contracts;
using SyncFactors.Domain;
using System.Text.Json;

namespace SyncFactors.Domain.Tests;

public sealed class DashboardSnapshotServiceTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-05-03T14:30:00Z");

    [Fact]
    public async Task GetSnapshotAsync_UsesIdleStatus_WhenRuntimeStatusIsMissing()
    {
        var service = new DashboardSnapshotService(
            new StubRuntimeStatusStore(null),
            new StubRunRepository([]),
            new FixedTimeProvider(Now));

        var snapshot = await service.GetSnapshotAsync(CancellationToken.None);

        Assert.Equal("Idle", snapshot.Status.Status);
        Assert.Equal("NotStarted", snapshot.Status.Stage);
        Assert.False(snapshot.RequiresAttention);
        Assert.Equal(Now, snapshot.CheckedAt);
    }

    [Fact]
    public async Task GetSnapshotAsync_SelectsActiveRun_ByRuntimeRunId()
    {
        var run = CreateRun("run-2", "Succeeded", Now.AddMinutes(-5), Now.AddMinutes(-1));
        var service = new DashboardSnapshotService(
            new StubRuntimeStatusStore(new RuntimeStatus(
                "InProgress",
                "BulkSync",
                "run-2",
                "BulkSync",
                true,
                1,
                3,
                "10001",
                "Processing worker",
                Now.AddMinutes(-10),
                Now,
                null,
                null)),
            new StubRunRepository([CreateRun("run-1", "Succeeded", Now.AddHours(-1), Now.AddMinutes(-50)), run]),
            new FixedTimeProvider(Now));

        var snapshot = await service.GetSnapshotAsync(CancellationToken.None);

        Assert.Same(run, snapshot.ActiveRun);
        Assert.Same(run, snapshot.LastCompletedRun);
    }

    [Fact]
    public async Task GetSnapshotAsync_SelectsInProgressRun_WhenRuntimeRunIdDoesNotMatch()
    {
        var activeRun = CreateRun("run-active", "InProgress", Now.AddMinutes(-2), null);
        var completedRun = CreateRun("run-complete", "Succeeded", Now.AddMinutes(-20), Now.AddMinutes(-10));
        var service = new DashboardSnapshotService(
            new StubRuntimeStatusStore(new RuntimeStatus(
                "InProgress",
                "BulkSync",
                "missing-run",
                "BulkSync",
                false,
                2,
                5,
                "10002",
                null,
                Now.AddMinutes(-3),
                Now,
                null,
                null)),
            new StubRunRepository([completedRun, activeRun]),
            new FixedTimeProvider(Now));

        var snapshot = await service.GetSnapshotAsync(CancellationToken.None);

        Assert.Same(activeRun, snapshot.ActiveRun);
        Assert.Same(completedRun, snapshot.LastCompletedRun);
    }

    [Fact]
    public async Task GetSnapshotAsync_ReportsAttention_FromRuntimeErrorOrLastFailure()
    {
        var failedRun = CreateRun("run-failed", "Failed", Now.AddMinutes(-20), Now.AddMinutes(-12));
        var service = new DashboardSnapshotService(
            new StubRuntimeStatusStore(new RuntimeStatus(
                "Idle",
                "Failed",
                null,
                null,
                true,
                0,
                0,
                null,
                null,
                null,
                Now,
                null,
                null)),
            new StubRunRepository([failedRun, CreateRun("run-old", "Succeeded", Now.AddHours(-2), Now.AddHours(-1))]),
            new FixedTimeProvider(Now));

        var snapshot = await service.GetSnapshotAsync(CancellationToken.None);

        Assert.True(snapshot.RequiresAttention);
        Assert.Equal("Last completed run run-failed failed.", snapshot.AttentionMessage);
    }

    private static RunSummary CreateRun(
        string runId,
        string status,
        DateTimeOffset startedAt,
        DateTimeOffset? completedAt)
    {
        return new RunSummary(
            RunId: runId,
            Path: null,
            ArtifactType: "BulkRun",
            ConfigPath: null,
            MappingConfigPath: null,
            Mode: "BulkSync",
            DryRun: true,
            Status: status,
            StartedAt: startedAt,
            CompletedAt: completedAt,
            DurationSeconds: null,
            ProcessedWorkers: 0,
            TotalWorkers: 0,
            Creates: 0,
            Updates: 0,
            Enables: 0,
            Disables: 0,
            GraveyardMoves: 0,
            Deletions: 0,
            Quarantined: 0,
            Conflicts: 0,
            GuardrailFailures: 0,
            ManualReview: 0,
            Unchanged: 0);
    }

    private sealed class StubRuntimeStatusStore(RuntimeStatus? status) : IRuntimeStatusStore
    {
        public Task<RuntimeStatus?> GetCurrentAsync(CancellationToken cancellationToken)
        {
            _ = cancellationToken;
            return Task.FromResult(status);
        }

        public Task SaveAsync(RuntimeStatus status, CancellationToken cancellationToken)
        {
            _ = status;
            _ = cancellationToken;
            throw new NotSupportedException();
        }

        public Task<bool> TryStartAsync(RuntimeStatus status, CancellationToken cancellationToken)
        {
            _ = status;
            _ = cancellationToken;
            throw new NotSupportedException();
        }
    }

    private sealed class StubRunRepository(IReadOnlyList<RunSummary> runs) : IRunRepository
    {
        public Task<IReadOnlyList<RunSummary>> ListRunsAsync(CancellationToken cancellationToken)
        {
            _ = cancellationToken;
            return Task.FromResult(runs);
        }

        public Task<RunDetail?> GetRunAsync(string runId, CancellationToken cancellationToken)
        {
            _ = runId;
            _ = cancellationToken;
            throw new NotSupportedException();
        }

        public Task<WorkerPreviewResult?> GetWorkerPreviewAsync(string runId, CancellationToken cancellationToken)
        {
            _ = runId;
            _ = cancellationToken;
            throw new NotSupportedException();
        }

        public Task<IReadOnlyList<WorkerPreviewHistoryItem>> ListWorkerPreviewHistoryAsync(string workerId, int take, CancellationToken cancellationToken)
        {
            _ = workerId;
            _ = take;
            _ = cancellationToken;
            throw new NotSupportedException();
        }

        public Task SaveRunAsync(RunRecord run, CancellationToken cancellationToken)
        {
            _ = run;
            _ = cancellationToken;
            throw new NotSupportedException();
        }

        public Task ReplaceRunEntriesAsync(string runId, IReadOnlyList<RunEntryRecord> entries, CancellationToken cancellationToken)
        {
            _ = runId;
            _ = entries;
            _ = cancellationToken;
            throw new NotSupportedException();
        }

        public Task AppendRunEntryAsync(RunEntryRecord entry, CancellationToken cancellationToken)
        {
            _ = entry;
            _ = cancellationToken;
            throw new NotSupportedException();
        }

        public Task<IReadOnlyList<RunEntry>> GetRunEntriesAsync(
            string runId,
            string? bucket,
            string? workerId,
            string? reason,
            string? filter,
            string? employmentStatus,
            string? entryId,
            int skip,
            int take,
            CancellationToken cancellationToken)
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
            throw new NotSupportedException();
        }

        public Task<IReadOnlyList<ChangedAttributeTotal>> GetRunEntryAttributeTotalsAsync(
            string runId,
            string? bucket,
            string? workerId,
            string? reason,
            string? filter,
            string? employmentStatus,
            string? entryId,
            CancellationToken cancellationToken)
        {
            _ = runId;
            _ = bucket;
            _ = workerId;
            _ = reason;
            _ = filter;
            _ = employmentStatus;
            _ = entryId;
            _ = cancellationToken;
            throw new NotSupportedException();
        }

        public Task<int> CountRunEntriesAsync(
            string runId,
            string? bucket,
            string? workerId,
            string? reason,
            string? filter,
            string? employmentStatus,
            string? entryId,
            CancellationToken cancellationToken)
        {
            _ = runId;
            _ = bucket;
            _ = workerId;
            _ = reason;
            _ = filter;
            _ = employmentStatus;
            _ = entryId;
            _ = cancellationToken;
            throw new NotSupportedException();
        }
        public Task<int> CountWorkerRunHistoryAsync(string workerId, CancellationToken cancellationToken) => Task.FromResult(0);

        public Task<IReadOnlyList<WorkerRunHistoryItem>> ListWorkerRunHistoryAsync(string workerId, int skip, int take, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<WorkerRunHistoryItem>>([]);

        public Task<int> PruneTerminalRunsStartedBeforeAsync(DateTimeOffset cutoffUtc, CancellationToken cancellationToken) => Task.FromResult(0);

        public Task<bool> VacuumIfNeededAsync(DateTimeOffset nowUtc, long minimumFreeBytes, TimeSpan minimumInterval, CancellationToken cancellationToken) => Task.FromResult(false);

        public Task<IReadOnlyList<EmploymentStatusTotal>> GetRunEntryEmploymentStatusTotalsAsync(string runId, string? bucket, string? workerId, string? reason, string? filter, string? employmentStatus, string? entryId, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<EmploymentStatusTotal>>([]);

    }

    private sealed class FixedTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => utcNow;
    }
}

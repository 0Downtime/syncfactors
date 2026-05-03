using SyncFactors.Contracts;
using SyncFactors.Domain;
using System.Text.Json;

namespace SyncFactors.Domain.Tests;

public sealed class RunLifecycleServiceTests
{
    [Fact]
    public async Task StartRunAsync_SavesStartingRuntimeStatus()
    {
        var runtimeStatusStore = new CapturingRuntimeStatusStore();
        var runRepository = new CapturingRunRepository(runDetail: null);
        var service = new RunLifecycleService(runtimeStatusStore, runRepository);

        await service.StartRunAsync(
            runId: "run-1",
            mode: "BulkSync",
            dryRun: true,
            runTrigger: "AdHoc",
            requestedBy: "test",
            totalWorkers: 0,
            initialAction: "Starting queued request req-1",
            cancellationToken: CancellationToken.None);

        var savedRuntime = Assert.Single(runtimeStatusStore.SavedStatuses);
        Assert.Equal("InProgress", savedRuntime.Status);
        Assert.Equal("Starting", savedRuntime.Stage);
        Assert.Equal("BulkSync", savedRuntime.Mode);
        Assert.Equal(0, savedRuntime.ProcessedWorkers);
        Assert.Equal(0, savedRuntime.TotalWorkers);
        Assert.Equal("Starting queued request req-1", savedRuntime.LastAction);
    }

    [Fact]
    public async Task CancelRunAsync_ResetsRuntimeProgress()
    {
        var runtimeStatusStore = new CapturingRuntimeStatusStore();
        var runRepository = new CapturingRunRepository(
            new RunDetail(
                new RunSummary(
                    RunId: "run-1",
                    Path: null,
                    ArtifactType: "BulkRun",
                    ConfigPath: null,
                    MappingConfigPath: null,
                    Mode: "BulkSync",
                    DryRun: true,
                    Status: "InProgress",
                    StartedAt: DateTimeOffset.Parse("2026-04-15T12:00:00Z"),
                    CompletedAt: null,
                    DurationSeconds: null,
                    ProcessedWorkers: 4,
                    TotalWorkers: 9,
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
                    Unchanged: 0),
                ParseJson("""{"kind":"bulkRun"}"""),
                BucketCounts: new Dictionary<string, int>()));
        var service = new RunLifecycleService(runtimeStatusStore, runRepository);
        var startedAt = DateTimeOffset.Parse("2026-04-15T12:00:00Z");

        await service.CancelRunAsync(
            runId: "run-1",
            mode: "BulkSync",
            dryRun: true,
            processedWorkers: 4,
            totalWorkers: 9,
            currentWorkerId: "10004",
            reason: "Run canceled by operator.",
            tally: new RunTally(0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0),
            report: ParseJson("""{"kind":"bulkRun"}"""),
            startedAt: startedAt,
            cancellationToken: CancellationToken.None);

        var savedRuntime = Assert.Single(runtimeStatusStore.SavedStatuses);
        Assert.Equal("Idle", savedRuntime.Status);
        Assert.Equal("Canceled", savedRuntime.Stage);
        Assert.Equal(0, savedRuntime.ProcessedWorkers);
        Assert.Equal(0, savedRuntime.TotalWorkers);
        Assert.Null(savedRuntime.CurrentWorkerId);
    }

    [Fact]
    public async Task CompleteRunAsync_PrunesHistoryOlderThanConfiguredRetention()
    {
        var runtimeStatusStore = new CapturingRuntimeStatusStore();
        var runRepository = new CapturingRunRepository(runDetail: null);
        runRepository.PrunedRunCount = 1;
        var now = DateTimeOffset.Parse("2026-04-30T15:00:00Z");
        var service = new RunLifecycleService(
            runtimeStatusStore,
            runRepository,
            new RunHistoryRetentionSettings(
                RetentionDays: 3,
                VacuumEnabled: true,
                VacuumMinimumFreedMegabytes: 64,
                VacuumMinimumIntervalHours: 12),
            new FixedTimeProvider(now));

        await service.CompleteRunAsync(
            runId: "run-1",
            mode: "BulkSync",
            dryRun: false,
            totalWorkers: 1,
            tally: new RunTally(0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 1),
            report: ParseJson("""{"kind":"bulkRun"}"""),
            startedAt: now.AddMinutes(-5),
            cancellationToken: CancellationToken.None);

        Assert.Equal(now.AddDays(-3), runRepository.LastPruneCutoffUtc);
        Assert.Equal(now, runRepository.LastVacuumNowUtc);
        Assert.Equal(64L * 1024L * 1024L, runRepository.LastVacuumMinimumFreeBytes);
        Assert.Equal(TimeSpan.FromHours(12), runRepository.LastVacuumMinimumInterval);
    }

    [Fact]
    public async Task ExecutePlannedRunAsync_PersistsPlanEntriesAndCompletionStatus()
    {
        var runtimeStatusStore = new CapturingRuntimeStatusStore();
        var runRepository = new CapturingRunRepository(runDetail: null);
        var startedAt = DateTimeOffset.Parse("2026-04-30T15:00:00Z");
        var plan = new ScaffoldRunPlanner().CreateBootstrapPlan(startedAt);
        var service = new RunLifecycleService(
            runtimeStatusStore,
            runRepository,
            new RunHistoryRetentionSettings(RetentionDays: 0));

        await service.ExecutePlannedRunAsync(plan, CancellationToken.None);

        Assert.Collection(
            runRepository.SavedRuns,
            first => Assert.Equal("InProgress", first.Status),
            second => Assert.Equal("Succeeded", second.Status));
        Assert.Equal(plan.Entries, runRepository.ReplacedEntries);
        Assert.Collection(
            runtimeStatusStore.SavedStatuses,
            first => Assert.Equal("Planning", first.Stage),
            second =>
            {
                Assert.Equal("Idle", second.Status);
                Assert.Equal("Completed", second.Stage);
                Assert.Equal(plan.TotalWorkers, second.ProcessedWorkers);
            });
    }

    [Fact]
    public async Task RecordProgressAsync_UsesExistingRunMetadataAndTally()
    {
        var runtimeStatusStore = new CapturingRuntimeStatusStore();
        var existing = CreateRunDetail(
            "run-1",
            artifactType: "FullSync",
            runTrigger: "Scheduled",
            requestedBy: "scheduler");
        var runRepository = new CapturingRunRepository(existing);
        var service = new RunLifecycleService(runtimeStatusStore, runRepository);

        await service.RecordProgressAsync(
            runId: "run-1",
            mode: "FullSync",
            dryRun: false,
            processedWorkers: 3,
            totalWorkers: 7,
            currentWorkerId: "10003",
            lastAction: "Updated worker 10003",
            tally: new RunTally(1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11),
            cancellationToken: CancellationToken.None);

        var savedRun = Assert.Single(runRepository.SavedRuns);
        Assert.Equal("FullSync", savedRun.ArtifactType);
        Assert.Equal("Scheduled", savedRun.RunTrigger);
        Assert.Equal("scheduler", savedRun.RequestedBy);
        Assert.Equal(3, savedRun.Enables);
        Assert.Equal(11, savedRun.Unchanged);

        var savedRuntime = Assert.Single(runtimeStatusStore.SavedStatuses);
        Assert.Equal("InProgress", savedRuntime.Status);
        Assert.Equal("FullSync", savedRuntime.Stage);
        Assert.Equal("10003", savedRuntime.CurrentWorkerId);
        Assert.Equal("Updated worker 10003", savedRuntime.LastAction);
    }

    [Fact]
    public async Task FailRunAsync_PreservesExistingRunMetadataAndRecordsFailureRuntime()
    {
        var runtimeStatusStore = new CapturingRuntimeStatusStore();
        var startedAt = DateTimeOffset.Parse("2026-04-30T15:00:00Z");
        var runRepository = new CapturingRunRepository(CreateRunDetail(
            "run-1",
            artifactType: "BulkRun",
            runTrigger: "ManualRetry",
            requestedBy: "operator"));
        var service = new RunLifecycleService(
            runtimeStatusStore,
            runRepository,
            new RunHistoryRetentionSettings(RetentionDays: 0));

        await service.FailRunAsync(
            runId: "run-1",
            mode: "BulkSync",
            dryRun: true,
            processedWorkers: 4,
            totalWorkers: 9,
            currentWorkerId: "10004",
            errorMessage: "Directory write failed.",
            tally: new RunTally(0, 1, 0, 0, 0, 0, 0, 1, 0, 0, 2),
            report: ParseJson("""{"kind":"bulkRun","status":"failed"}"""),
            startedAt: startedAt,
            cancellationToken: CancellationToken.None);

        var savedRun = Assert.Single(runRepository.SavedRuns);
        Assert.Equal("Failed", savedRun.Status);
        Assert.Equal("ManualRetry", savedRun.RunTrigger);
        Assert.Equal("operator", savedRun.RequestedBy);
        Assert.Equal(1, savedRun.Updates);
        Assert.Equal(1, savedRun.Conflicts);

        var savedRuntime = Assert.Single(runtimeStatusStore.SavedStatuses);
        Assert.Equal("Failed", savedRuntime.Status);
        Assert.Equal("BulkSync", savedRuntime.Stage);
        Assert.Equal("Directory write failed.", savedRuntime.ErrorMessage);
        Assert.Equal("10004", savedRuntime.CurrentWorkerId);
    }

    [Fact]
    public async Task CancelRunAsync_UsesDefaultMessage_WhenReasonIsBlank()
    {
        var runtimeStatusStore = new CapturingRuntimeStatusStore();
        var runRepository = new CapturingRunRepository(runDetail: null);
        var service = new RunLifecycleService(
            runtimeStatusStore,
            runRepository,
            new RunHistoryRetentionSettings(RetentionDays: 0));

        await service.CancelRunAsync(
            runId: "run-1",
            mode: "BulkSync",
            dryRun: true,
            processedWorkers: 1,
            totalWorkers: 2,
            currentWorkerId: "10001",
            reason: " ",
            tally: new RunTally(0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 1),
            report: ParseJson("""{"kind":"bulkRun","status":"canceled"}"""),
            startedAt: DateTimeOffset.Parse("2026-04-30T15:00:00Z"),
            cancellationToken: CancellationToken.None);

        var savedRun = Assert.Single(runRepository.SavedRuns);
        Assert.Equal("Canceled", savedRun.Status);
        Assert.Equal("AdHoc", savedRun.RunTrigger);

        var savedRuntime = Assert.Single(runtimeStatusStore.SavedStatuses);
        Assert.Equal("Run canceled.", savedRuntime.LastAction);
        Assert.Equal(0, savedRuntime.ProcessedWorkers);
        Assert.Equal(0, savedRuntime.TotalWorkers);
    }

    [Fact]
    public async Task AppendRunEntryAsync_ForwardsEntryToRepository()
    {
        var entry = new RunEntryRecord(
            EntryId: "entry-1",
            RunId: "run-1",
            Bucket: "updates",
            BucketIndex: 0,
            WorkerId: "10001",
            SamAccountName: "lab10001",
            Reason: null,
            ReviewCategory: null,
            ReviewCaseType: null,
            StartedAt: DateTimeOffset.Parse("2026-04-30T15:00:00Z"),
            Item: ParseJson("""{"workerId":"10001"}"""));
        var runRepository = new CapturingRunRepository(runDetail: null);
        var service = new RunLifecycleService(new CapturingRuntimeStatusStore(), runRepository);

        await service.AppendRunEntryAsync("run-1", entry, CancellationToken.None);

        Assert.Same(entry, Assert.Single(runRepository.AppendedEntries));
    }

    private static JsonElement ParseJson(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    private static RunDetail CreateRunDetail(
        string runId,
        string artifactType,
        string runTrigger,
        string? requestedBy)
    {
        return new RunDetail(
            new RunSummary(
                RunId: runId,
                Path: null,
                ArtifactType: artifactType,
                ConfigPath: "sync-config.json",
                MappingConfigPath: "mapping.json",
                Mode: "BulkSync",
                DryRun: true,
                Status: "InProgress",
                StartedAt: DateTimeOffset.Parse("2026-04-30T15:00:00Z"),
                CompletedAt: null,
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
                Unchanged: 0,
                RunTrigger: runTrigger,
                RequestedBy: requestedBy),
            ParseJson("""{"kind":"bulkRun","existing":true}"""),
            BucketCounts: new Dictionary<string, int>());
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
            _ = status;
            _ = cancellationToken;
            throw new NotSupportedException();
        }

        public Task SaveAsync(RuntimeStatus status, CancellationToken cancellationToken)
        {
            _ = cancellationToken;
            SavedStatuses.Add(status);
            return Task.CompletedTask;
        }
    }

    private sealed class CapturingRunRepository(RunDetail? runDetail) : IRunRepository
    {
        public DateTimeOffset? LastPruneCutoffUtc { get; private set; }
        public DateTimeOffset? LastVacuumNowUtc { get; private set; }
        public long? LastVacuumMinimumFreeBytes { get; private set; }
        public TimeSpan? LastVacuumMinimumInterval { get; private set; }
        public int PrunedRunCount { get; set; }
        public List<RunRecord> SavedRuns { get; } = [];
        public IReadOnlyList<RunEntryRecord>? ReplacedEntries { get; private set; }
        public List<RunEntryRecord> AppendedEntries { get; } = [];

        public Task<IReadOnlyList<RunSummary>> ListRunsAsync(CancellationToken cancellationToken)
        {
            _ = cancellationToken;
            return Task.FromResult<IReadOnlyList<RunSummary>>([]);
        }

        public Task<RunDetail?> GetRunAsync(string runId, CancellationToken cancellationToken)
        {
            _ = cancellationToken;
            return Task.FromResult(
                runDetail is not null && string.Equals(runDetail.Run.RunId, runId, StringComparison.Ordinal)
                    ? runDetail
                    : null);
        }

        public Task<WorkerPreviewResult?> GetWorkerPreviewAsync(string runId, CancellationToken cancellationToken)
        {
            _ = runId;
            _ = cancellationToken;
            return Task.FromResult<WorkerPreviewResult?>(null);
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
            _ = runId;
            _ = cancellationToken;
            ReplacedEntries = entries;
            return Task.CompletedTask;
        }

        public Task AppendRunEntryAsync(RunEntryRecord entry, CancellationToken cancellationToken)
        {
            _ = cancellationToken;
            AppendedEntries.Add(entry);
            return Task.CompletedTask;
        }

        public Task<int> PruneTerminalRunsStartedBeforeAsync(DateTimeOffset cutoffUtc, CancellationToken cancellationToken)
        {
            _ = cancellationToken;
            LastPruneCutoffUtc = cutoffUtc;
            return Task.FromResult(PrunedRunCount);
        }

        public Task<bool> VacuumIfNeededAsync(
            DateTimeOffset nowUtc,
            long minimumFreeBytes,
            TimeSpan minimumInterval,
            CancellationToken cancellationToken)
        {
            _ = cancellationToken;
            LastVacuumNowUtc = nowUtc;
            LastVacuumMinimumFreeBytes = minimumFreeBytes;
            LastVacuumMinimumInterval = minimumInterval;
            return Task.FromResult(true);
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
    }

    private sealed class FixedTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => utcNow;
    }
}

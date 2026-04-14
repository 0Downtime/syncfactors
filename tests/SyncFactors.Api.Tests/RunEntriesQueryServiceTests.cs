using System.Text.Json;
using SyncFactors.Api;
using SyncFactors.Contracts;
using SyncFactors.Domain;

namespace SyncFactors.Api.Tests;

public sealed class RunEntriesQueryServiceTests
{
    [Fact]
    public async Task LoadAsync_ReturnsPagedEntriesAndAttributeTotals()
    {
        var repository = new StubRunRepository();
        var service = new RunEntriesQueryService(repository);

        var result = await service.LoadAsync("bulk-1", "updates", "worker-1", null, "email", null, 2, 25, CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal("bulk-1", repository.LastTotalsRunId);
        Assert.Equal("bulk-1", repository.LastEmploymentTotalsRunId);
        Assert.Equal("updates", repository.LastTotalsBucket);
        Assert.Equal("worker-1", repository.LastTotalsWorkerId);
        Assert.Equal("email", repository.LastTotalsFilter);
        Assert.Equal(120, result!.Total);
        Assert.Equal(2, result.Page);
        Assert.Equal(25, result.PageSize);
        Assert.Equal(25, result.Entries.Count);
        Assert.Collection(
            result.EmploymentStatusTotals,
            total =>
            {
                Assert.Equal("64300", total.Code);
                Assert.Equal(80, total.Count);
            },
            total =>
            {
                Assert.Equal("64304", total.Code);
                Assert.Equal(40, total.Count);
            });
        Assert.Collection(
            result.AttributeTotals,
            total =>
            {
                Assert.Equal("email", total.Attribute);
                Assert.Equal(12, total.Count);
            },
            total =>
            {
                Assert.Equal("cn", total.Attribute);
                Assert.Equal(4, total.Count);
            });
    }

    [Fact]
    public async Task LoadAsync_ReturnsNullWhenRunDoesNotExist()
    {
        var service = new RunEntriesQueryService(new MissingRunRepository());

        var result = await service.LoadAsync("missing-run", null, null, null, null, null, 1, 50, CancellationToken.None);

        Assert.Null(result);
    }

    private sealed class StubRunRepository : IRunRepository
    {
        public string? LastTotalsRunId { get; private set; }

        public string? LastTotalsBucket { get; private set; }

        public string? LastTotalsWorkerId { get; private set; }

        public string? LastTotalsFilter { get; private set; }

        public string? LastEmploymentTotalsRunId { get; private set; }

        public Task<IReadOnlyList<RunSummary>> ListRunsAsync(CancellationToken cancellationToken)
        {
            _ = cancellationToken;
            return Task.FromResult<IReadOnlyList<RunSummary>>([]);
        }

        public Task<RunDetail?> GetRunAsync(string runId, CancellationToken cancellationToken)
        {
            _ = cancellationToken;
            return Task.FromResult<RunDetail?>(
                new RunDetail(
                    new RunSummary(
                        RunId: runId,
                        Path: null,
                        ArtifactType: "BulkRun",
                        ConfigPath: null,
                        MappingConfigPath: null,
                        Mode: "BulkSync",
                        DryRun: true,
                        Status: "Succeeded",
                        StartedAt: DateTimeOffset.UtcNow,
                        CompletedAt: DateTimeOffset.UtcNow,
                        DurationSeconds: 10,
                        ProcessedWorkers: 120,
                        TotalWorkers: 120,
                        Creates: 10,
                        Updates: 100,
                        Enables: 0,
                        Disables: 0,
                        GraveyardMoves: 0,
                        Deletions: 0,
                        Quarantined: 0,
                        Conflicts: 10,
                        GuardrailFailures: 0,
                        ManualReview: 0,
                        Unchanged: 0,
                        SyncScope: "Delta"),
                    JsonDocument.Parse("""{"kind":"bulkRun"}""").RootElement.Clone(),
                    new Dictionary<string, int> { ["updates"] = 100, ["conflicts"] = 10, ["creates"] = 10 }));
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

        public Task<IReadOnlyList<RunEntry>> GetRunEntriesAsync(string runId, string? bucket, string? workerId, string? reason, string? filter, string? entryId, int skip, int take, CancellationToken cancellationToken)
        {
            _ = runId;
            _ = bucket;
            _ = workerId;
            _ = reason;
            _ = filter;
            _ = entryId;
            _ = cancellationToken;
            return Task.FromResult<IReadOnlyList<RunEntry>>(Enumerable.Range(1, take).Select(index =>
                new RunEntry(
                    EntryId: $"entry-{skip + index}",
                    RunId: runId,
                    ArtifactType: "BulkRun",
                    Mode: "BulkSync",
                    Bucket: "updates",
                    BucketLabel: "Updates",
                    WorkerId: $"worker-{skip + index}",
                    SamAccountName: null,
                    Reason: null,
                    ReviewCategory: null,
                    ReviewCaseType: null,
                    StartedAt: DateTimeOffset.UtcNow,
                    ChangeCount: 0,
                    OperationSummary: null,
                    FailureSummary: null,
                    PrimarySummary: null,
                    TopChangedAttributes: [],
                    DiffRows: [],
                    Item: JsonDocument.Parse("""{}""").RootElement.Clone())).ToArray());
        }

        public Task<IReadOnlyList<ChangedAttributeTotal>> GetRunEntryAttributeTotalsAsync(string runId, string? bucket, string? workerId, string? reason, string? filter, string? entryId, CancellationToken cancellationToken)
        {
            LastTotalsRunId = runId;
            LastTotalsBucket = bucket;
            LastTotalsWorkerId = workerId;
            LastTotalsFilter = filter;
            _ = reason;
            _ = entryId;
            _ = cancellationToken;
            return Task.FromResult<IReadOnlyList<ChangedAttributeTotal>>(
            [
                new("email", 12),
                new("cn", 4)
            ]);
        }

        public Task<IReadOnlyList<EmploymentStatusTotal>> GetRunEntryEmploymentStatusTotalsAsync(string runId, string? bucket, string? workerId, string? reason, string? filter, string? entryId, CancellationToken cancellationToken)
        {
            LastEmploymentTotalsRunId = runId;
            _ = bucket;
            _ = workerId;
            _ = reason;
            _ = filter;
            _ = entryId;
            _ = cancellationToken;
            return Task.FromResult<IReadOnlyList<EmploymentStatusTotal>>(
            [
                new("64300", 80),
                new("64304", 40)
            ]);
        }

        public Task<int> CountRunEntriesAsync(string runId, string? bucket, string? workerId, string? reason, string? filter, string? entryId, CancellationToken cancellationToken)
        {
            _ = runId;
            _ = bucket;
            _ = workerId;
            _ = reason;
            _ = filter;
            _ = entryId;
            _ = cancellationToken;
            return Task.FromResult(120);
        }
    }

    private sealed class MissingRunRepository : IRunRepository
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

        public Task<IReadOnlyList<RunEntry>> GetRunEntriesAsync(string runId, string? bucket, string? workerId, string? reason, string? filter, string? entryId, int skip, int take, CancellationToken cancellationToken)
        {
            _ = runId;
            _ = bucket;
            _ = workerId;
            _ = reason;
            _ = filter;
            _ = entryId;
            _ = skip;
            _ = take;
            _ = cancellationToken;
            return Task.FromResult<IReadOnlyList<RunEntry>>([]);
        }

        public Task<IReadOnlyList<ChangedAttributeTotal>> GetRunEntryAttributeTotalsAsync(string runId, string? bucket, string? workerId, string? reason, string? filter, string? entryId, CancellationToken cancellationToken)
        {
            _ = runId;
            _ = bucket;
            _ = workerId;
            _ = reason;
            _ = filter;
            _ = entryId;
            _ = cancellationToken;
            return Task.FromResult<IReadOnlyList<ChangedAttributeTotal>>([]);
        }

        public Task<int> CountRunEntriesAsync(string runId, string? bucket, string? workerId, string? reason, string? filter, string? entryId, CancellationToken cancellationToken)
        {
            _ = runId;
            _ = bucket;
            _ = workerId;
            _ = reason;
            _ = filter;
            _ = entryId;
            _ = cancellationToken;
            return Task.FromResult(0);
        }
    }
}

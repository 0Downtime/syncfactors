using System.Text.Json;
using SyncFactors.Api;
using SyncFactors.Contracts;
using SyncFactors.Domain;

namespace SyncFactors.Api.Tests;

public sealed class ExceptionQueueQueryServiceTests
{
    [Fact]
    public async Task LoadAsync_ReturnsFailedRunsAndExceptionEntries()
    {
        var repository = new StubRunRepository();
        var service = new ExceptionQueueQueryService(repository);

        var result = await service.LoadAsync(null, null, 1, 10, CancellationToken.None);

        Assert.Equal(4, result.Total);
        Assert.Equal(1, result.Summary["failedRuns"]);
        Assert.Equal(1, result.Summary["manualReview"]);
        Assert.Equal(1, result.Summary["conflicts"]);
        Assert.Equal(1, result.Summary["guardrailFailures"]);
        Assert.Collection(
            result.Items,
            item => Assert.Equal("manualReview", item.QueueType),
            item => Assert.Equal("conflicts", item.QueueType),
            item => Assert.Equal("guardrailFailures", item.QueueType),
            item => Assert.Equal("failedRuns", item.QueueType));
    }

    [Fact]
    public async Task LoadAsync_PaginatesAcrossRunsAndBuckets()
    {
        var repository = new StubRunRepository();
        var service = new ExceptionQueueQueryService(repository);

        var result = await service.LoadAsync(null, null, 2, 2, CancellationToken.None);

        Assert.Equal(4, result.Total);
        Assert.Equal(2, result.Page);
        Assert.Equal(2, result.TotalPages);
        Assert.Collection(
            result.Items,
            item => Assert.Equal("guardrailFailures", item.QueueType),
            item => Assert.Equal("failedRuns", item.QueueType));
    }

    [Fact]
    public async Task LoadAsync_FiltersToSelectedQueue()
    {
        var repository = new StubRunRepository();
        var service = new ExceptionQueueQueryService(repository);

        var result = await service.LoadAsync("conflicts", null, 1, 10, CancellationToken.None);

        var item = Assert.Single(result.Items);
        Assert.Equal("conflicts", item.QueueType);
        Assert.Equal(1, result.Total);
    }

    private sealed class StubRunRepository : IRunRepository
    {
        private readonly IReadOnlyList<RunSummary> _runs =
        [
            CreateRun("run-2", "Succeeded", manualReview: 1, conflicts: 1, guardrailFailures: 1),
            CreateRun("run-1", "Failed")
        ];

        public Task<IReadOnlyList<RunSummary>> ListRunsAsync(CancellationToken cancellationToken)
        {
            _ = cancellationToken;
            return Task.FromResult(_runs);
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
            _ = workerId;
            _ = reason;
            _ = filter;
            _ = employmentStatus;
            _ = entryId;
            _ = cancellationToken;

            var allEntries = bucket is null || string.Equals(runId, "run-1", StringComparison.OrdinalIgnoreCase)
                ? []
                : new[] { CreateEntry(runId, bucket) };
            return Task.FromResult<IReadOnlyList<RunEntry>>(allEntries.Skip(skip).Take(take).ToArray());
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
            return Task.FromResult<IReadOnlyList<ChangedAttributeTotal>>([]);
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
            _ = workerId;
            _ = reason;
            _ = filter;
            _ = employmentStatus;
            _ = entryId;
            _ = cancellationToken;
            return Task.FromResult(string.Equals(runId, "run-2", StringComparison.OrdinalIgnoreCase) && bucket is not null ? 1 : 0);
        }

        private static RunSummary CreateRun(
            string runId,
            string status,
            int manualReview = 0,
            int conflicts = 0,
            int guardrailFailures = 0) =>
            new(
                RunId: runId,
                Path: null,
                ArtifactType: "SyncReport",
                ConfigPath: null,
                MappingConfigPath: null,
                Mode: "BulkSync",
                DryRun: false,
                Status: status,
                StartedAt: runId == "run-2" ? DateTimeOffset.UtcNow : DateTimeOffset.UtcNow.AddMinutes(-5),
                CompletedAt: DateTimeOffset.UtcNow,
                DurationSeconds: 10,
                ProcessedWorkers: 1,
                TotalWorkers: 1,
                Creates: 0,
                Updates: 0,
                Enables: 0,
                Disables: 0,
                GraveyardMoves: 0,
                Deletions: 0,
                Quarantined: 0,
                Conflicts: conflicts,
                GuardrailFailures: guardrailFailures,
                ManualReview: manualReview,
                Unchanged: 0);

        private static RunEntry CreateEntry(string runId, string bucket) =>
            new(
                EntryId: $"{bucket}-1",
                RunId: runId,
                ArtifactType: "SyncReport",
                Mode: "BulkSync",
                Bucket: bucket,
                BucketLabel: bucket,
                WorkerId: "10001",
                SamAccountName: "worker.one",
                Reason: "Needs operator review",
                ReviewCategory: null,
                ReviewCaseType: null,
                StartedAt: DateTimeOffset.UtcNow,
                ChangeCount: 0,
                OperationSummary: null,
                FailureSummary: null,
                PrimarySummary: null,
                TopChangedAttributes: [],
                DiffRows: [],
                Item: JsonDocument.Parse("{}").RootElement.Clone());
    }
}

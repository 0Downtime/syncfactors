using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using SyncFactors.Api.Pages.Runs;
using SyncFactors.Contracts;
using SyncFactors.Domain;
using System.Text.Json;

namespace SyncFactors.Api.Tests;

public sealed class RunDetailModelTests
{
    [Fact]
    public async Task OnGetAsync_LoadsRequestedPageOfEntries()
    {
        var repository = new StubRunRepository();
        var model = new DetailModel(repository)
        {
            RunId = "bulk-1",
            PageNumber = 2
        };

        var result = await model.OnGetAsync(CancellationToken.None);

        Assert.IsType<PageResult>(result);
        Assert.Equal(50, repository.LastSkip);
        Assert.Equal(50, repository.LastTake);
        Assert.Equal(120, model.TotalEntries);
        Assert.Equal(3, model.TotalPages);
        Assert.Equal(2, model.PageNumber);
        Assert.True(model.HasPreviousPage);
        Assert.True(model.HasNextPage);
        Assert.Equal(50, model.Entries.Count);
    }

    private sealed class StubRunRepository : IRunRepository
    {
        public int LastSkip { get; private set; }

        public int LastTake { get; private set; }

        public Task<IReadOnlyList<RunSummary>> ListRunsAsync(CancellationToken cancellationToken)
        {
            _ = cancellationToken;
            return Task.FromResult<IReadOnlyList<RunSummary>>([]);
        }

        public Task<RunDetail?> GetRunAsync(string runId, CancellationToken cancellationToken)
        {
            _ = runId;
            _ = cancellationToken;
            return Task.FromResult<RunDetail?>(
                new RunDetail(
                    new RunSummary(
                        RunId: "bulk-1",
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
            LastSkip = skip;
            LastTake = take;
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
}

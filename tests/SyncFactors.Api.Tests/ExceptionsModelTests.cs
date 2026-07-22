using Microsoft.AspNetCore.Mvc.RazorPages;
using SyncFactors.Api;
using SyncFactors.Api.Pages;
using SyncFactors.Contracts;
using SyncFactors.Domain;
using System.Text.Json;

namespace SyncFactors.Api.Tests;

public sealed class ExceptionsModelTests
{
    [Fact]
    public async Task OnGetAsync_LoadsQueueAndNormalizesPaging()
    {
        var run = CreateRun("run-failed", status: "Failed", manualReview: 2, conflicts: 3, guardrailFailures: 4);
        var repository = new StubRunRepository(
            [run],
            new Dictionary<string, IReadOnlyList<RunEntry>>(StringComparer.OrdinalIgnoreCase)
            {
                ["manualReview"] =
                [
                    CreateEntry("entry-1", run.RunId, "manualReview", "Manual Review", workerId: "10001", changeCount: 0, reviewCategory: "Lifecycle")
                ]
            });
        var model = new ExceptionsModel(new ExceptionQueueQueryService(repository))
        {
            QueueType = "manualReview",
            Search = "10001",
            PageNumber = 0
        };

        await model.OnGetAsync(CancellationToken.None);

        Assert.Equal("manualReview", model.Queue.QueueType);
        Assert.Equal(1, model.PageNumber);
        Assert.False(model.HasPreviousPage);
        Assert.False(model.HasNextPage);
        Assert.Equal(2, model.GetSummaryCount("manualReview"));
        Assert.Equal(3, model.GetSummaryCount("conflicts"));
        Assert.Equal(4, model.GetSummaryCount("guardrailFailures"));
        Assert.Equal(0, model.GetSummaryCount("missing"));
        var item = Assert.Single(model.Queue.Items);
        Assert.Equal("Manual Review", item.Label);
        Assert.Equal("Lifecycle", item.Detail);
    }

    [Fact]
    public async Task LoadAsync_IncludesFailedRunsAndPaginatesEntries()
    {
        var failedRun = CreateRun("run-failed", status: "Failed", requestedBy: "operator@example.com");
        var healthyRun = CreateRun("run-healthy", status: "Succeeded", conflicts: 3);
        var repository = new StubRunRepository(
            [failedRun, healthyRun],
            new Dictionary<string, IReadOnlyList<RunEntry>>(StringComparer.OrdinalIgnoreCase)
            {
                ["conflicts"] =
                [
                    CreateEntry("conflict-1", healthyRun.RunId, "conflicts", "Conflicts", workerId: "10001", changeCount: 2, topChanges: ["department", "title"]),
                    CreateEntry("conflict-2", healthyRun.RunId, "conflicts", "Conflicts", workerId: "10002", changeCount: 1, failureSummary: "Duplicate SAM account")
                ]
            });
        var service = new ExceptionQueueQueryService(repository);

        var failedRuns = await service.LoadAsync("failedRuns", "operator", page: 1, pageSize: 10, CancellationToken.None);
        var conflictsPageTwo = await service.LoadAsync("conflicts", search: null, page: 2, pageSize: 1, CancellationToken.None);

        var failedItem = Assert.Single(failedRuns.Items);
        Assert.Equal("failedRuns", failedItem.QueueType);
        Assert.Equal("Requested by operator@example.com", failedItem.Detail);
        Assert.Equal(1, failedRuns.Total);

        var conflictItem = Assert.Single(conflictsPageTwo.Items);
        Assert.Equal("Duplicate SAM account", conflictItem.Summary);
        Assert.Equal(2, conflictsPageTwo.Total);
        Assert.Equal(2, conflictsPageTwo.Page);
        Assert.True(conflictsPageTwo.TotalPages >= 2);
    }

    private static RunSummary CreateRun(
        string runId,
        string status,
        int manualReview = 0,
        int conflicts = 0,
        int guardrailFailures = 0,
        string? requestedBy = null)
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
            StartedAt: DateTimeOffset.Parse("2026-04-20T12:00:00Z"),
            CompletedAt: DateTimeOffset.Parse("2026-04-20T12:05:00Z"),
            DurationSeconds: 300,
            ProcessedWorkers: 3,
            TotalWorkers: 5,
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
            Unchanged: 0,
            RunTrigger: "AdHoc",
            RequestedBy: requestedBy);
    }

    private static RunEntry CreateEntry(
        string entryId,
        string runId,
        string bucket,
        string bucketLabel,
        string workerId,
        int changeCount,
        string? failureSummary = null,
        string? reviewCategory = null,
        IReadOnlyList<string>? topChanges = null)
    {
        return new RunEntry(
            EntryId: entryId,
            RunId: runId,
            ArtifactType: "BulkRun",
            Mode: "BulkSync",
            Bucket: bucket,
            BucketLabel: bucketLabel,
            WorkerId: workerId,
            SamAccountName: $"lab{workerId}",
            Reason: null,
            ReviewCategory: reviewCategory,
            ReviewCaseType: null,
            StartedAt: DateTimeOffset.Parse("2026-04-20T12:01:00Z"),
            ChangeCount: changeCount,
            OperationSummary: null,
            FailureSummary: failureSummary,
            PrimarySummary: null,
            TopChangedAttributes: topChanges ?? [],
            DiffRows: [],
            Item: ParseJson("""{"workerId":"10001"}"""));
    }

    private static JsonElement ParseJson(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    private sealed class StubRunRepository(
        IReadOnlyList<RunSummary> runs,
        IReadOnlyDictionary<string, IReadOnlyList<RunEntry>> entriesByBucket) : IRunRepository
    {
        public Task<IReadOnlyList<RunSummary>> ListRunsAsync(CancellationToken cancellationToken)
        {
            _ = cancellationToken;
            return Task.FromResult(runs);
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
            _ = employmentStatus;
            _ = entryId;
            _ = cancellationToken;
            return Task.FromResult(Filtered(runId, bucket, filter).Count);
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
            _ = workerId;
            _ = reason;
            _ = employmentStatus;
            _ = entryId;
            _ = cancellationToken;
            return Task.FromResult<IReadOnlyList<RunEntry>>(Filtered(runId, bucket, filter).Skip(skip).Take(take).ToArray());
        }

        private IReadOnlyList<RunEntry> Filtered(string runId, string? bucket, string? filter)
        {
            if (bucket is null || !entriesByBucket.TryGetValue(bucket, out var entries))
            {
                return [];
            }

            return entries
                .Where(entry => string.Equals(entry.RunId, runId, StringComparison.Ordinal))
                .Where(entry => string.IsNullOrWhiteSpace(filter) ||
                    (entry.WorkerId?.Contains(filter, StringComparison.OrdinalIgnoreCase) ?? false) ||
                    (entry.SamAccountName?.Contains(filter, StringComparison.OrdinalIgnoreCase) ?? false))
                .ToArray();
        }

        public Task<RunDetail?> GetRunAsync(string runId, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<WorkerPreviewResult?> GetWorkerPreviewAsync(string runId, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<IReadOnlyList<WorkerPreviewHistoryItem>> ListWorkerPreviewHistoryAsync(string workerId, int take, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task SaveRunAsync(RunRecord run, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task ReplaceRunEntriesAsync(string runId, IReadOnlyList<RunEntryRecord> entries, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task AppendRunEntryAsync(RunEntryRecord entry, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<IReadOnlyList<ChangedAttributeTotal>> GetRunEntryAttributeTotalsAsync(string runId, string? bucket, string? workerId, string? reason, string? filter, string? employmentStatus, string? entryId, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<int> CountWorkerRunHistoryAsync(string workerId, CancellationToken cancellationToken) => Task.FromResult(0);

        public Task<IReadOnlyList<WorkerRunHistoryItem>> ListWorkerRunHistoryAsync(string workerId, int skip, int take, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<WorkerRunHistoryItem>>([]);

        public Task<int> PruneTerminalRunsStartedBeforeAsync(DateTimeOffset cutoffUtc, CancellationToken cancellationToken) => Task.FromResult(0);

        public Task<bool> VacuumIfNeededAsync(DateTimeOffset nowUtc, long minimumFreeBytes, TimeSpan minimumInterval, CancellationToken cancellationToken) => Task.FromResult(false);

        public Task<IReadOnlyList<EmploymentStatusTotal>> GetRunEntryEmploymentStatusTotalsAsync(string runId, string? bucket, string? workerId, string? reason, string? filter, string? employmentStatus, string? entryId, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<EmploymentStatusTotal>>([]);

    }
}

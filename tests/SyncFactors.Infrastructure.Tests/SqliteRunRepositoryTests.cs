using System.Text.Json;
using SyncFactors.Contracts;
using SyncFactors.Infrastructure;

namespace SyncFactors.Infrastructure.Tests;

public sealed class SqliteRunRepositoryTests
{
    [Fact]
    public async Task ListRunsAsync_BackfillsBucketCountsFromRunEntriesWhenMaterializedSummaryLags()
    {
        var databasePath = Path.Combine(Path.GetTempPath(), $"syncfactors-run-repo-{Guid.NewGuid():N}.db");

        try
        {
            var repository = await CreateRepositoryAsync(databasePath);
            var runId = "bulk-canceled-1";
            var startedAt = DateTimeOffset.Parse("2026-04-02T13:30:00Z");

            await repository.SaveRunAsync(
                CreateRunRecord(runId, startedAt),
                CancellationToken.None);

            await repository.AppendRunEntryAsync(CreateEntry(runId, "creates", 0, "10001"), CancellationToken.None);
            await repository.AppendRunEntryAsync(CreateEntry(runId, "updates", 1, "10002"), CancellationToken.None);
            await repository.AppendRunEntryAsync(CreateEntry(runId, "conflicts", 2, "10003"), CancellationToken.None);

            var runs = await repository.ListRunsAsync(CancellationToken.None);
            var run = Assert.Single(runs);

            Assert.Equal(1, run.Creates);
            Assert.Equal(1, run.Updates);
            Assert.Equal(1, run.Conflicts);
            Assert.Equal(0, run.ManualReview);
            Assert.Equal(2, run.ProcessedWorkers);
            Assert.Equal(3, run.TotalWorkers);
        }
        finally
        {
            File.Delete(databasePath);
        }
    }

    [Fact]
    public async Task GetRunAsync_BackfillsBucketCountsFromRunEntriesWhenMaterializedSummaryLags()
    {
        var databasePath = Path.Combine(Path.GetTempPath(), $"syncfactors-run-detail-{Guid.NewGuid():N}.db");

        try
        {
            var repository = await CreateRepositoryAsync(databasePath);
            var runId = "bulk-canceled-2";
            var startedAt = DateTimeOffset.Parse("2026-04-02T13:45:00Z");

            await repository.SaveRunAsync(
                CreateRunRecord(runId, startedAt),
                CancellationToken.None);

            await repository.AppendRunEntryAsync(CreateEntry(runId, "creates", 0, "10001"), CancellationToken.None);
            await repository.AppendRunEntryAsync(CreateEntry(runId, "guardrailFailures", 1, "10002"), CancellationToken.None);
            await repository.AppendRunEntryAsync(CreateEntry(runId, "manualReview", 2, "10003"), CancellationToken.None);

            var detail = await repository.GetRunAsync(runId, CancellationToken.None);

            Assert.NotNull(detail);
            Assert.Equal(1, detail!.Run.Creates);
            Assert.Equal(1, detail.Run.GuardrailFailures);
            Assert.Equal(1, detail.Run.ManualReview);
            Assert.Equal(1, detail.BucketCounts["creates"]);
            Assert.Equal(1, detail.BucketCounts["guardrailFailures"]);
            Assert.Equal(1, detail.BucketCounts["manualReview"]);
        }
        finally
        {
            File.Delete(databasePath);
        }
    }

    [Fact]
    public async Task ListRunsAsync_ExcludesWorkerPreviewArtifactsFromRecentRuns()
    {
        var databasePath = Path.Combine(Path.GetTempPath(), $"syncfactors-run-filter-{Guid.NewGuid():N}.db");

        try
        {
            var repository = await CreateRepositoryAsync(databasePath);
            var bulkRunId = "bulk-run-1";
            var previewRunId = "preview-run-1";
            var startedAt = DateTimeOffset.Parse("2026-04-02T14:00:00Z");

            await repository.SaveRunAsync(
                CreateRunRecord(bulkRunId, startedAt),
                CancellationToken.None);

            await repository.SaveRunAsync(
                CreateRunRecord(
                    previewRunId,
                    startedAt.AddMinutes(1),
                    artifactType: "WorkerPreview",
                    mode: "WorkerPreview"),
                CancellationToken.None);

            var runs = await repository.ListRunsAsync(CancellationToken.None);

            var run = Assert.Single(runs);
            Assert.Equal(bulkRunId, run.RunId);
        }
        finally
        {
            File.Delete(databasePath);
        }
    }

    private static async Task<SqliteRunRepository> CreateRepositoryAsync(string databasePath)
    {
        var pathResolver = new SqlitePathResolver(databasePath);
        var initializer = new SqliteDatabaseInitializer(pathResolver);
        await initializer.InitializeAsync(CancellationToken.None);
        return new SqliteRunRepository(pathResolver);
    }

    private static RunRecord CreateRunRecord(
        string runId,
        DateTimeOffset startedAt,
        string artifactType = "BulkRun",
        string mode = "BulkSync")
    {
        return new RunRecord(
            RunId: runId,
            Path: null,
            ArtifactType: artifactType,
            ConfigPath: null,
            MappingConfigPath: null,
            Mode: mode,
            DryRun: false,
            Status: "Canceled",
            StartedAt: startedAt,
            CompletedAt: startedAt.AddMinutes(1),
            DurationSeconds: 60,
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
            Report: JsonSerializer.SerializeToElement(new { kind = "bulkRun", runId }),
            RunTrigger: "AdHoc",
            RequestedBy: "test");
    }

    private static RunEntryRecord CreateEntry(string runId, string bucket, int bucketIndex, string workerId)
    {
        return new RunEntryRecord(
            EntryId: $"{runId}:{bucket}:{workerId}:{bucketIndex}",
            RunId: runId,
            Bucket: bucket,
            BucketIndex: bucketIndex,
            WorkerId: workerId,
            SamAccountName: $"user{workerId}",
            Reason: $"{bucket} reason",
            ReviewCategory: null,
            ReviewCaseType: null,
            StartedAt: DateTimeOffset.UtcNow,
            Item: JsonSerializer.SerializeToElement(new
            {
                workerId,
                bucket,
                reason = $"{bucket} reason"
            }));
    }
}

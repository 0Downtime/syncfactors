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
    public async Task GetRunAsync_BucketCounts_HideZeroQuarantinedAndExposeLifecycleBuckets()
    {
        var databasePath = Path.Combine(Path.GetTempPath(), $"syncfactors-run-buckets-{Guid.NewGuid():N}.db");

        try
        {
            var repository = await CreateRepositoryAsync(databasePath);
            var runId = "bulk-buckets-1";
            var startedAt = DateTimeOffset.Parse("2026-04-02T13:50:00Z");

            await repository.SaveRunAsync(
                CreateRunRecord(runId, startedAt) with
                {
                    Creates = 1,
                    Updates = 2,
                    Enables = 3,
                    Disables = 4,
                    GraveyardMoves = 5,
                    Deletions = 6,
                    Conflicts = 7,
                    ManualReview = 8,
                    GuardrailFailures = 9,
                    Unchanged = 10,
                    Quarantined = 0
                },
                CancellationToken.None);

            var detail = await repository.GetRunAsync(runId, CancellationToken.None);

            Assert.NotNull(detail);
            Assert.Equal(
                ["creates", "updates", "enables", "disables", "graveyardMoves", "deletions", "conflicts", "manualReview", "guardrailFailures", "unchanged"],
                detail!.BucketCounts.Keys.ToArray());
            Assert.DoesNotContain("quarantined", detail.BucketCounts.Keys);
        }
        finally
        {
            File.Delete(databasePath);
        }
    }

    [Fact]
    public async Task GetRunAsync_BucketCounts_KeepLegacyQuarantinedWhenPresent()
    {
        var databasePath = Path.Combine(Path.GetTempPath(), $"syncfactors-run-buckets-legacy-{Guid.NewGuid():N}.db");

        try
        {
            var repository = await CreateRepositoryAsync(databasePath);
            var runId = "bulk-buckets-legacy-1";
            var startedAt = DateTimeOffset.Parse("2026-04-02T13:55:00Z");

            await repository.SaveRunAsync(
                CreateRunRecord(runId, startedAt) with
                {
                    Quarantined = 2,
                    Unchanged = 1
                },
                CancellationToken.None);

            var detail = await repository.GetRunAsync(runId, CancellationToken.None);

            Assert.NotNull(detail);
            Assert.Equal(2, detail!.BucketCounts["quarantined"]);
            Assert.Equal(["quarantined", "unchanged"], detail.BucketCounts.Where(pair => pair.Value > 0).Select(pair => pair.Key).ToArray());
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

    [Fact]
    public async Task GetRunEntryAttributeTotalsAsync_CountsChangedAttributesAcrossMatchingEntries()
    {
        var databasePath = Path.Combine(Path.GetTempPath(), $"syncfactors-run-attribute-totals-{Guid.NewGuid():N}.db");

        try
        {
            var repository = await CreateRepositoryAsync(databasePath);
            var runId = "bulk-attrs-1";
            var startedAt = DateTimeOffset.Parse("2026-04-02T14:15:00Z");

            await repository.SaveRunAsync(
                CreateRunRecord(runId, startedAt),
                CancellationToken.None);

            await repository.AppendRunEntryAsync(CreateEntry(
                runId,
                "updates",
                0,
                "10001",
                item: new
                {
                    workerId = "10001",
                    changedAttributeDetails = new[]
                    {
                        new { targetAttribute = "cn", sourceField = "preferredName", currentAdValue = "Old Name", proposedValue = "New Name" },
                        new { targetAttribute = "mail", sourceField = "email", currentAdValue = "before@example.com", proposedValue = "after@example.com" }
                    }
                }),
                CancellationToken.None);

            await repository.AppendRunEntryAsync(CreateEntry(
                runId,
                "updates",
                1,
                "10002",
                item: new
                {
                    workerId = "10002",
                    changedAttributeDetails = new[]
                    {
                        new { targetAttribute = "mail", sourceField = "email", currentAdValue = "old@example.com", proposedValue = "new@example.com" }
                    }
                }),
                CancellationToken.None);

            await repository.AppendRunEntryAsync(CreateEntry(
                runId,
                "unchanged",
                2,
                "10003",
                item: new
                {
                    workerId = "10003",
                    attributeRows = new[]
                    {
                        new { targetAttribute = "department", sourceField = "department", currentAdValue = "IT", proposedValue = "IT", changed = false }
                    }
                }),
                CancellationToken.None);

            var totals = await repository.GetRunEntryAttributeTotalsAsync(runId, null, null, null, null, null, null, CancellationToken.None);

            Assert.Collection(
                totals,
                total =>
                {
                    Assert.Equal("mail", total.Attribute);
                    Assert.Equal(2, total.Count);
                },
                total =>
                {
                    Assert.Equal("cn", total.Attribute);
                    Assert.Equal(1, total.Count);
                });
        }
        finally
        {
            File.Delete(databasePath);
        }
    }

    [Fact]
    public async Task GetRunEntryAttributeTotalsAsync_HonorsBucketWorkerAndFilter()
    {
        var databasePath = Path.Combine(Path.GetTempPath(), $"syncfactors-run-attribute-filters-{Guid.NewGuid():N}.db");

        try
        {
            var repository = await CreateRepositoryAsync(databasePath);
            var runId = "bulk-attrs-2";
            var startedAt = DateTimeOffset.Parse("2026-04-02T14:30:00Z");

            await repository.SaveRunAsync(
                CreateRunRecord(runId, startedAt),
                CancellationToken.None);

            await repository.AppendRunEntryAsync(CreateEntry(
                runId,
                "updates",
                0,
                "10001",
                item: new
                {
                    workerId = "10001",
                    note = "email sync",
                    changedAttributeDetails = new[]
                    {
                        new { targetAttribute = "mail", sourceField = "email", currentAdValue = "before@example.com", proposedValue = "after@example.com" }
                    }
                }),
                CancellationToken.None);

            await repository.AppendRunEntryAsync(CreateEntry(
                runId,
                "updates",
                1,
                "20002",
                item: new
                {
                    workerId = "20002",
                    note = "email sync",
                    changedAttributeDetails = new[]
                    {
                        new { targetAttribute = "mail", sourceField = "email", currentAdValue = "old@example.com", proposedValue = "new@example.com" },
                        new { targetAttribute = "cn", sourceField = "preferredName", currentAdValue = "A", proposedValue = "B" }
                    }
                }),
                CancellationToken.None);

            await repository.AppendRunEntryAsync(CreateEntry(
                runId,
                "creates",
                2,
                "10003",
                item: new
                {
                    workerId = "10003",
                    note = "email sync",
                    changedAttributeDetails = new[]
                    {
                        new { targetAttribute = "mail", sourceField = "email", currentAdValue = (string?)null, proposedValue = "create@example.com" }
                    }
                }),
                CancellationToken.None);

            var totals = await repository.GetRunEntryAttributeTotalsAsync(runId, "updates", "100", null, "email", null, null, CancellationToken.None);

            var total = Assert.Single(totals);
            Assert.Equal("mail", total.Attribute);
            Assert.Equal(1, total.Count);
        }
        finally
        {
            File.Delete(databasePath);
        }
    }

    [Fact]
    public async Task GetRunEntryAttributeTotalsAsync_GroupsAttributeNamesCaseInsensitively()
    {
        var databasePath = Path.Combine(Path.GetTempPath(), $"syncfactors-run-attribute-case-{Guid.NewGuid():N}.db");

        try
        {
            var repository = await CreateRepositoryAsync(databasePath);
            var runId = "bulk-attrs-3";
            var startedAt = DateTimeOffset.Parse("2026-04-02T14:45:00Z");

            await repository.SaveRunAsync(
                CreateRunRecord(runId, startedAt),
                CancellationToken.None);

            await repository.AppendRunEntryAsync(CreateEntry(
                runId,
                "updates",
                0,
                "10001",
                item: new
                {
                    workerId = "10001",
                    changedAttributeDetails = new[]
                    {
                        new { targetAttribute = "CN", sourceField = "preferredName", currentAdValue = "Old 1", proposedValue = "New 1" }
                    }
                }),
                CancellationToken.None);

            await repository.AppendRunEntryAsync(CreateEntry(
                runId,
                "updates",
                1,
                "10002",
                item: new
                {
                    workerId = "10002",
                    changedAttributeDetails = new[]
                    {
                        new { targetAttribute = "cn", sourceField = "preferredName", currentAdValue = "Old 2", proposedValue = "New 2" }
                    }
                }),
                CancellationToken.None);

            var total = Assert.Single(await repository.GetRunEntryAttributeTotalsAsync(runId, null, null, null, null, null, null, CancellationToken.None));

            Assert.Equal("CN", total.Attribute);
            Assert.Equal(2, total.Count);
        }
        finally
        {
            File.Delete(databasePath);
        }
    }

    [Fact]
    public async Task GetRunEntryEmploymentStatusTotalsAsync_CountsStatusesAcrossMatchingEntries()
    {
        var databasePath = Path.Combine(Path.GetTempPath(), $"syncfactors-run-status-totals-{Guid.NewGuid():N}.db");

        try
        {
            var repository = await CreateRepositoryAsync(databasePath);
            var runId = "bulk-status-1";
            var startedAt = DateTimeOffset.Parse("2026-04-02T15:15:00Z");

            await repository.SaveRunAsync(
                CreateRunRecord(runId, startedAt),
                CancellationToken.None);

            await repository.AppendRunEntryAsync(CreateEntry(
                runId,
                "updates",
                0,
                "10001",
                item: new
                {
                    workerId = "10001",
                    emplStatus = "64300",
                    note = "email sync"
                }),
                CancellationToken.None);

            await repository.AppendRunEntryAsync(CreateEntry(
                runId,
                "updates",
                1,
                "10002",
                item: new
                {
                    workerId = "10002",
                    emplStatus = "64300",
                    note = "email sync"
                }),
                CancellationToken.None);

            await repository.AppendRunEntryAsync(CreateEntry(
                runId,
                "creates",
                2,
                "10003",
                item: new
                {
                    workerId = "10003",
                    emplStatus = "64304",
                    note = "email sync"
                }),
                CancellationToken.None);

            await repository.AppendRunEntryAsync(CreateEntry(
                runId,
                "creates",
                3,
                "10004",
                item: new
                {
                    workerId = "10004",
                    note = "missing status"
                }),
                CancellationToken.None);

            var totals = await repository.GetRunEntryEmploymentStatusTotalsAsync(runId, null, null, null, "email", null, null, CancellationToken.None);

            Assert.Collection(
                totals,
                total =>
                {
                    Assert.Equal("64300", total.Code);
                    Assert.Equal(2, total.Count);
                },
                total =>
                {
                    Assert.Equal("64304", total.Code);
                    Assert.Equal(1, total.Count);
                });
        }
        finally
        {
            File.Delete(databasePath);
        }
    }

    [Fact]
    public async Task GetRunEntriesAsync_FiltersByEmploymentStatus()
    {
        var databasePath = Path.Combine(Path.GetTempPath(), $"syncfactors-run-status-filter-{Guid.NewGuid():N}.db");

        try
        {
            var repository = await CreateRepositoryAsync(databasePath);
            var runId = "bulk-status-filter-1";
            var startedAt = DateTimeOffset.Parse("2026-04-02T15:30:00Z");

            await repository.SaveRunAsync(
                CreateRunRecord(runId, startedAt),
                CancellationToken.None);

            await repository.AppendRunEntryAsync(CreateEntry(
                runId,
                "updates",
                0,
                "10001",
                item: new
                {
                    workerId = "10001",
                    emplStatus = "64300"
                }),
                CancellationToken.None);

            await repository.AppendRunEntryAsync(CreateEntry(
                runId,
                "updates",
                1,
                "10002",
                item: new
                {
                    workerId = "10002",
                    emplStatus = "64307"
                }),
                CancellationToken.None);

            var entries = await repository.GetRunEntriesAsync(runId, null, null, null, null, "64307", null, 0, 10, CancellationToken.None);
            var total = await repository.CountRunEntriesAsync(runId, null, null, null, null, "64307", null, CancellationToken.None);

            var entry = Assert.Single(entries);
            Assert.Equal("10002", entry.WorkerId);
            Assert.Equal(1, total);
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

    private static RunEntryRecord CreateEntry(string runId, string bucket, int bucketIndex, string workerId, object? item = null)
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
            Item: JsonSerializer.SerializeToElement(item ?? new
            {
                workerId,
                bucket,
                reason = $"{bucket} reason"
            }));
    }
}

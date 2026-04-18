using SyncFactors.Api;
using SyncFactors.Contracts;

namespace SyncFactors.Api.Tests;

public sealed class UiRunFormattingTests
{
    [Theory]
    [InlineData("Succeeded", "good")]
    [InlineData("Failed", "bad")]
    [InlineData("InProgress", "info")]
    [InlineData("Pending", "warn")]
    [InlineData("CancelRequested", "warn")]
    [InlineData("Canceled", "dim")]
    [InlineData("Cancelled", "dim")]
    [InlineData("Unknown", "neutral")]
    public void StatusBadgeTone_ReturnsConsistentTone(string status, string expectedTone)
    {
        var actualTone = UiRunFormatting.StatusBadgeTone(status);

        Assert.Equal(expectedTone, actualTone);
    }

    [Fact]
    public void RunCardSummary_UsesProcessedAndTotalWorkers()
    {
        var run = CreateRunSummary(processedWorkers: 7, totalWorkers: 9);

        var summary = UiRunFormatting.RunCardSummary(run);

        Assert.Equal("Succeeded · BulkSync · 7 / 9 workers", summary);
    }

    [Fact]
    public void RunBucketSummary_IncludesLegacyQuarantinedBucket()
    {
        var run = CreateRunSummary(
            creates: 2,
            quarantined: 3,
            conflicts: 1,
            manualReview: 4,
            guardrailFailures: 5);

        var summary = UiRunFormatting.RunBucketSummary(run);

        Assert.Equal("2 creates · 3 quarantined · 1 conflicts · 4 manual review · 5 guardrails", summary);
    }

    private static RunSummary CreateRunSummary(
        int processedWorkers = 12,
        int totalWorkers = 12,
        int creates = 0,
        int updates = 0,
        int enables = 0,
        int disables = 0,
        int graveyardMoves = 0,
        int deletions = 0,
        int quarantined = 0,
        int conflicts = 0,
        int guardrailFailures = 0,
        int manualReview = 0,
        int unchanged = 0) =>
        new(
            RunId: "bulk-1",
            Path: null,
            ArtifactType: "BulkRun",
            ConfigPath: null,
            MappingConfigPath: null,
            Mode: "BulkSync",
            DryRun: true,
            Status: "Succeeded",
            StartedAt: DateTimeOffset.Parse("2026-04-18T12:00:00Z"),
            CompletedAt: DateTimeOffset.Parse("2026-04-18T12:10:00Z"),
            DurationSeconds: 600,
            ProcessedWorkers: processedWorkers,
            TotalWorkers: totalWorkers,
            Creates: creates,
            Updates: updates,
            Enables: enables,
            Disables: disables,
            GraveyardMoves: graveyardMoves,
            Deletions: deletions,
            Quarantined: quarantined,
            Conflicts: conflicts,
            GuardrailFailures: guardrailFailures,
            ManualReview: manualReview,
            Unchanged: unchanged,
            SyncScope: "Full sync");
}

using Microsoft.Extensions.Logging;
using SyncFactors.Domain;
using SyncFactors.Infrastructure;

namespace SyncFactors.MockSuccessFactors;

public sealed class MockPlannerComparisonService(
    SuccessFactorsWorkerSource workerSource,
    IWorkerPlanningService planningService,
    ILogger<MockPlannerComparisonService> logger)
{
    public async Task<MockAdminPlannerBucketSnapshot> CompareAsync(string workerId, CancellationToken cancellationToken)
    {
        try
        {
            var worker = await workerSource.GetWorkerAsync(workerId, cancellationToken);
            if (worker is null)
            {
                return new MockAdminPlannerBucketSnapshot(
                    Status: "unavailable",
                    Bucket: null,
                    Label: null,
                    Reason: null,
                    ReviewCaseType: null,
                    Error: $"Worker '{workerId}' was not resolved by the sync planner source.");
            }

            var plan = await planningService.PlanAsync(worker, logPath: null, cancellationToken);
            return new MockAdminPlannerBucketSnapshot(
                Status: "available",
                Bucket: plan.Bucket,
                Label: MockFixtureSummaryReporter.DescribeProvisioningBucket(plan.Bucket),
                Reason: plan.Reason,
                ReviewCaseType: plan.ReviewCaseType,
                Error: null);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Planner bucket comparison failed. WorkerId={WorkerId}", workerId);
            return new MockAdminPlannerBucketSnapshot(
                Status: "error",
                Bucket: null,
                Label: null,
                Reason: null,
                ReviewCaseType: null,
                Error: ex.Message);
        }
    }
}

internal sealed class MockDeltaSyncService : IDeltaSyncService
{
    public Task<DeltaSyncWindow> GetWindowAsync(CancellationToken cancellationToken)
    {
        _ = cancellationToken;
        return Task.FromResult(new DeltaSyncWindow(
            Enabled: false,
            HasCheckpoint: false,
            Filter: null,
            DeltaField: string.Empty,
            CheckpointUtc: null,
            EffectiveSinceUtc: null));
    }

    public Task RecordSuccessfulRunAsync(DateTimeOffset checkpointUtc, CancellationToken cancellationToken)
    {
        _ = checkpointUtc;
        _ = cancellationToken;
        return Task.CompletedTask;
    }
}

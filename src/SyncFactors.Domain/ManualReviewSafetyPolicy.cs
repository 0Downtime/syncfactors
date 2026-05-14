using SyncFactors.Contracts;

namespace SyncFactors.Domain;

internal static class ManualReviewSafetyPolicy
{
    public const string ReviewCategory = "SafetyPolicy";
    public const string DisableReviewCaseType = "DisableRequiresManualReview";
    public const string DeletionReviewCaseType = "DeletionRequiresManualReview";
    public const string DisableReviewReason = "Disable operation requires manual review by safety policy.";
    public const string DeletionReviewReason = "Deletion operation requires manual review by safety policy.";

    public static bool RequiresDisableReview(
        WorkerRunSettings settings,
        string bucket,
        IReadOnlyList<DirectoryOperation> operations)
    {
        return settings.ManualReviewDisables &&
               (string.Equals(bucket, "disables", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(bucket, "graveyardMoves", StringComparison.OrdinalIgnoreCase)) &&
               operations.Any(operation => string.Equals(operation.Kind, "DisableUser", StringComparison.OrdinalIgnoreCase));
    }

    public static PlannedWorkerAction ToDisableManualReview(PlannedWorkerAction plan)
    {
        return plan with
        {
            Bucket = "manualReview",
            ReviewCategory = ReviewCategory,
            ReviewCaseType = DisableReviewCaseType,
            Reason = DisableReviewReason,
            CanAutoApply = false
        };
    }
}

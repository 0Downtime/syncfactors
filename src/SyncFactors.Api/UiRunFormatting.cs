using SyncFactors.Contracts;

namespace SyncFactors.Api;

public static class UiRunFormatting
{
    public static string StatusBadgeTone(string? status) =>
        (status ?? string.Empty).Trim().ToLowerInvariant() switch
        {
            "succeeded" => "good",
            "failed" => "bad",
            "inprogress" => "info",
            "planned" or "pending" or "cancelrequested" => "warn",
            "canceled" or "cancelled" => "dim",
            _ => "neutral",
        };

    public static string RunCardSummary(RunSummary run) =>
        $"{run.Status} · {run.Mode} · {run.ProcessedWorkers} / {run.TotalWorkers} workers";

    public static string RunBucketSummary(RunSummary run)
    {
        var parts = new List<string>();
        if (run.Creates > 0) parts.Add($"{run.Creates} creates");
        if (run.Updates > 0) parts.Add($"{run.Updates} updates");
        if (run.Enables > 0) parts.Add($"{run.Enables} enables");
        if (run.Disables > 0) parts.Add($"{run.Disables} disables");
        if (run.GraveyardMoves > 0) parts.Add($"{run.GraveyardMoves} graveyard moves");
        if (run.Deletions > 0) parts.Add($"{run.Deletions} deletions");
        if (run.Quarantined > 0) parts.Add($"{run.Quarantined} quarantined");
        if (run.Conflicts > 0) parts.Add($"{run.Conflicts} conflicts");
        if (run.ManualReview > 0) parts.Add($"{run.ManualReview} manual review");
        if (run.GuardrailFailures > 0) parts.Add($"{run.GuardrailFailures} guardrails");
        return parts.Count == 0 ? "No materialized bucket counts yet" : string.Join(" · ", parts);
    }
}

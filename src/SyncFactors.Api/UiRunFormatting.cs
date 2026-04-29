using SyncFactors.Contracts;
using System.Text.RegularExpressions;

namespace SyncFactors.Api;

public static partial class UiRunFormatting
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

    public static string DisplayLabel(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "Unknown";
        }

        var normalized = value.Trim().Replace('-', ' ').Replace('_', ' ');
        return WordBoundaryRegex().Replace(normalized, " ");
    }

    public static string RunDisplayName(RunSummary run)
    {
        var parts = new List<string> { RunKindLabel(run) };
        if (!string.IsNullOrWhiteSpace(run.SyncScope) &&
            !string.Equals(run.SyncScope, "Unknown", StringComparison.OrdinalIgnoreCase))
        {
            parts.Add(DisplayLabel(run.SyncScope));
        }

        parts.Add(UiDateTimeFormatter.FormatDateTime(run.StartedAt));
        return string.Join(" · ", parts);
    }

    public static string RuntimeDisplayName(RuntimeStatus status)
    {
        if (string.IsNullOrWhiteSpace(status.RunId))
        {
            return "None";
        }

        var parts = new List<string>
        {
            RunKindLabel(status.Mode, status.DryRun, artifactType: null)
        };

        if (status.StartedAt.HasValue)
        {
            parts.Add(UiDateTimeFormatter.FormatDateTime(status.StartedAt));
        }
        else
        {
            parts.Add(status.RunId);
        }

        return string.Join(" · ", parts);
    }

    public static string RunCardSummary(RunSummary run) =>
        $"{DisplayLabel(run.Status)} · {RunKindLabel(run)} · {run.ProcessedWorkers} / {run.TotalWorkers} workers";

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

    private static string RunKindLabel(RunSummary run) =>
        RunKindLabel(run.Mode, run.DryRun, run.ArtifactType);

    private static string RunKindLabel(string? mode, bool dryRun, string? artifactType)
    {
        if (string.Equals(artifactType, "WorkerPreview", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(mode, "Preview", StringComparison.OrdinalIgnoreCase))
        {
            return "Preview Snapshot";
        }

        if (string.Equals(mode, "BulkSync", StringComparison.OrdinalIgnoreCase))
        {
            return dryRun ? "Dry Run" : "Live Sync";
        }

        if (string.Equals(mode, "BulkSyncWithPrehireSweep", StringComparison.OrdinalIgnoreCase))
        {
            return dryRun ? "Dry Run + Due Prehires" : "Live Sync + Due Prehires";
        }

        var modeLabel = DisplayLabel(mode);
        return dryRun ? $"{modeLabel} Dry Run" : modeLabel;
    }

    [GeneratedRegex(@"(?<=[a-z0-9])(?=[A-Z])|(?<=[A-Z])(?=[A-Z][a-z])", RegexOptions.CultureInvariant)]
    private static partial Regex WordBoundaryRegex();
}

using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using SyncFactors.Api;
using SyncFactors.Contracts;
using SyncFactors.Domain;
using System.Text;
using System.Text.Json;

namespace SyncFactors.Api.Pages.Runs;

public sealed class DetailModel(RunEntriesQueryService queryService) : PageModel
{
    public const int EntriesPerPage = 15;
    private static readonly JsonSerializerOptions ExportSerializerOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    [BindProperty(SupportsGet = true)]
    public string RunId { get; set; } = string.Empty;

    [BindProperty(SupportsGet = true)]
    public string? Bucket { get; set; }

    [BindProperty(SupportsGet = true)]
    public string? WorkerId { get; set; }

    [BindProperty(SupportsGet = true)]
    public string? Filter { get; set; }

    [BindProperty(SupportsGet = true)]
    public int PageNumber { get; set; } = 1;

    public RunDetail? Run { get; private set; }

    public IReadOnlyList<RunEntry> Entries { get; private set; } = [];

    public IReadOnlyList<ChangedAttributeTotal> AttributeTotals { get; private set; } = [];

    public IReadOnlyList<EmploymentStatusTotal> EmploymentStatusTotals { get; private set; } = [];

    public IReadOnlyList<string> AvailableBuckets { get; private set; } = [];

    public int TotalEntries { get; private set; }

    public int TotalPages { get; private set; }

    public bool HasPreviousPage => PageNumber > 1;

    public bool HasNextPage => PageNumber < TotalPages;

    public bool IsPreviewRun =>
        Run is not null &&
        (string.Equals(Run.Run.ArtifactType, "WorkerPreview", StringComparison.OrdinalIgnoreCase) ||
         string.Equals(Run.Run.Mode, "Preview", StringComparison.OrdinalIgnoreCase));

    public bool IsDryRun =>
        Run is not null &&
        Run.Run.DryRun &&
        !IsPreviewRun;

    public RunContextDisplay DescribeRunContext()
    {
        if (Run is null)
        {
            return new RunContextDisplay("Unknown", "neutral", "Run context is not available.");
        }

        if (IsPreviewRun)
        {
            return new RunContextDisplay(
                "Preview Snapshot",
                "info",
                "This run is a saved preview. Entries describe planned outcomes only; nothing in this run wrote to Active Directory.");
        }

        if (IsDryRun)
        {
            return new RunContextDisplay(
                "Dry Run",
                "info",
                "This run staged what would happen during a real sync. Entries were evaluated, but no Active Directory writes were attempted.");
        }

        return new RunContextDisplay(
            "Real Sync",
            "good",
            "This run executed real sync work where needed. Use each entry status to see whether AD was updated, skipped, or failed.");
    }

    public FailureDiagnostics? GetFailureDiagnostics(RunEntry entry)
        => ActiveDirectoryFailureDiagnostics.Parse(entry.Reason ?? entry.FailureSummary);

    public string? GetFailureSummaryDisplay(RunEntry entry)
        => GetFailureDiagnostics(entry)?.Summary ?? entry.FailureSummary;

    public string? GetPrimarySummaryDisplay(RunEntry entry)
    {
        var primarySummary = entry.PrimarySummary?.Trim();
        if (string.IsNullOrWhiteSpace(primarySummary))
        {
            return null;
        }

        if (GetFailureDiagnostics(entry) is not null &&
            (StringEquals(primarySummary, entry.Reason) || StringEquals(primarySummary, entry.FailureSummary)))
        {
            return null;
        }

        var failureSummary = GetFailureSummaryDisplay(entry);
        return StringEquals(primarySummary, failureSummary) ? null : primarySummary;
    }

    public bool ShouldShowReason(RunEntry entry)
    {
        var reason = entry.Reason?.Trim();
        if (string.IsNullOrWhiteSpace(reason))
        {
            return false;
        }

        if (GetFailureDiagnostics(entry) is not null)
        {
            return false;
        }

        var primarySummary = GetPrimarySummaryDisplay(entry);
        var failureSummary = GetFailureSummaryDisplay(entry);
        return !StringEquals(reason, primarySummary) && !StringEquals(reason, failureSummary);
    }

    public EmploymentStatusInfo? GetEmploymentStatus(RunEntry entry)
        => EmploymentStatusDisplay.Describe(GetItemString(entry.Item, "emplStatus"));

    public EmploymentStatusInfo? DescribeEmploymentStatus(string? code)
        => EmploymentStatusDisplay.Describe(code);

    public string? GetEmploymentStatusDisplay(RunEntry entry)
        => GetEmploymentStatus(entry)?.Display;

    public EntryExecutionDisplay DescribeEntryExecution(RunEntry entry)
    {
        var action = GetItemString(entry.Item, "action");
        var applied = GetItemBoolean(entry.Item, "applied");
        var succeeded = GetItemBoolean(entry.Item, "succeeded");
        var operationCount = GetItemArrayCount(entry.Item, "operations");
        var changedCount = entry.ChangeCount;
        var facts = new List<EntryExecutionFact>
        {
            new("Run Type", DescribeRunContext().Label),
            new("Planned Action", DescribePlannedAction(entry, action, operationCount)),
            new("Directory Ops", FormatCount(operationCount, "directory operation")),
            new("Attribute Diffs", FormatCount(changedCount, "attribute diff"))
        };

        if (IsPreviewRun)
        {
            if (entry.Bucket is "manualReview")
            {
                facts.Add(new("Execution", "Not executed"));
                facts.Add(new("Result", "Manual review required"));
                return new EntryExecutionDisplay(
                    "Preview Review",
                    "warn",
                    "Preview found a blocker that requires operator review before any real sync should be attempted.",
                    facts);
            }

            if (entry.Bucket is "guardrailFailures")
            {
                facts.Add(new("Execution", "Not executed"));
                facts.Add(new("Result", "Guardrail blocked preview"));
                return new EntryExecutionDisplay(
                    "Preview Blocked",
                    "warn",
                    "Preview stopped at a guardrail or validation boundary. No AD change has happened.",
                    facts);
            }

            if (entry.Bucket is "conflicts")
            {
                facts.Add(new("Execution", "Not executed"));
                facts.Add(new("Result", "Preview planning failed"));
                return new EntryExecutionDisplay(
                    "Preview Failed",
                    "bad",
                    "Preview could not produce a clean plan for this worker. No AD change has happened.",
                    facts);
            }

            if (operationCount == 0 && changedCount == 0)
            {
                facts.Add(new("Execution", "Not executed"));
                facts.Add(new("Result", "No AD write would be required"));
                return new EntryExecutionDisplay(
                    "Preview No Action",
                    "neutral",
                    "Preview found the worker already in the target state. A real sync would not need to write to AD for this entry.",
                    facts);
            }

            facts.Add(new("Execution", "Not executed"));
            facts.Add(new("Result", "Would change AD on real sync"));
            return new EntryExecutionDisplay(
                "Preview Planned",
                "info",
                "Preview staged the planned AD outcome for this worker. Nothing has been written to AD yet.",
                facts);
        }

        if (IsDryRun)
        {
            if (entry.Bucket is "manualReview")
            {
                facts.Add(new("Execution", "Skipped"));
                facts.Add(new("Result", "Manual review required"));
                return new EntryExecutionDisplay(
                    "Needs Review",
                    "warn",
                    "Dry run stopped at manual review for this worker. No AD write was attempted.",
                    facts);
            }

            if (entry.Bucket is "guardrailFailures")
            {
                facts.Add(new("Execution", "Skipped"));
                facts.Add(new("Result", "Guardrail blocked execution"));
                return new EntryExecutionDisplay(
                    "Blocked",
                    "warn",
                    "Dry run hit a guardrail for this worker. No AD write was attempted.",
                    facts);
            }

            if (entry.Bucket is "conflicts")
            {
                facts.Add(new("Execution", "Not executed"));
                facts.Add(new("Result", "Dry run validation failed"));
                return new EntryExecutionDisplay(
                    "Dry Run Failed",
                    "bad",
                    "Dry run could not prepare or validate the AD change for this worker.",
                    facts);
            }

            if (operationCount == 0 && changedCount == 0)
            {
                facts.Add(new("Execution", "Not executed"));
                facts.Add(new("Result", "No AD write required"));
                return new EntryExecutionDisplay(
                    "No Action Needed",
                    "neutral",
                    "Dry run found the worker already in the target state. No AD write would be required.",
                    facts);
            }

            facts.Add(new("Execution", "Not executed"));
            facts.Add(new("Result", "Dry run only"));
            return new EntryExecutionDisplay(
                "Dry Run Planned",
                "info",
                "Dry run staged the AD work for this worker, but did not send anything to AD.",
                facts);
        }

        if (entry.Bucket is "manualReview")
        {
            facts.Add(new("Execution", "Skipped"));
            facts.Add(new("Result", "Manual review required"));
            return new EntryExecutionDisplay(
                "Needs Review",
                "warn",
                "Real sync skipped this worker because manual review is required before any AD write can occur.",
                facts);
        }

        if (entry.Bucket is "guardrailFailures")
        {
            facts.Add(new("Execution", "Skipped"));
            facts.Add(new("Result", "Guardrail blocked execution"));
            return new EntryExecutionDisplay(
                "Blocked",
                "warn",
                "Real sync skipped this worker because a guardrail blocked execution.",
                facts);
        }

        if (entry.Bucket is "conflicts")
        {
            facts.Add(new("Execution", applied == true ? "Attempted" : "Not applied"));
            facts.Add(new("Result", applied == true ? "AD write failed" : "Failed before AD write"));
            return new EntryExecutionDisplay(
                "Failed",
                "bad",
                applied == true
                    ? "Real sync attempted the AD change for this worker and it failed."
                    : "Real sync could not apply this worker. No AD write completed successfully.",
                facts);
        }

        if (applied == true && succeeded != false)
        {
            facts.Add(new("Execution", "Executed"));
            facts.Add(new("Result", "AD write succeeded"));
            return new EntryExecutionDisplay(
                "Applied",
                "good",
                operationCount > 0
                    ? $"Real sync applied {FormatCount(operationCount, "directory operation")} successfully."
                    : "Real sync applied the AD change successfully.",
                facts);
        }

        if (operationCount == 0 && string.IsNullOrWhiteSpace(action))
        {
            facts.Add(new("Execution", "No AD write required"));
            facts.Add(new("Result", "Already in target state"));
            return new EntryExecutionDisplay(
                "Already Compliant",
                "neutral",
                "The worker already matched the target lifecycle state. No AD write was required for this entry.",
                facts);
        }

        facts.Add(new("Execution", applied == true ? "Attempted" : "Not executed"));
        facts.Add(new("Result", succeeded == false ? "AD write failed" : "Not clearly recorded"));
        return new EntryExecutionDisplay(
            "Not Applied",
            "neutral",
            "This entry was categorized, but the stored run data does not show a completed AD write.",
            facts);
    }

    public async Task<IActionResult> OnGetAsync(CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(RunId))
        {
            return NotFound();
        }

        var result = await queryService.LoadAsync(RunId, Bucket, WorkerId, null, Filter, null, PageNumber, EntriesPerPage, cancellationToken);
        if (result is null)
        {
            return NotFound();
        }

        Run = result.Run;

        AvailableBuckets = Run.BucketCounts
            .Where(pair => pair.Value > 0)
            .Select(pair => pair.Key)
            .ToArray();

        TotalEntries = result.Total;
        AttributeTotals = result.AttributeTotals;
        EmploymentStatusTotals = result.EmploymentStatusTotals;
        Entries = result.Entries;
        PageNumber = result.Page;
        TotalPages = Math.Max(1, (int)Math.Ceiling(TotalEntries / (double)EntriesPerPage));
        return Page();
    }

    public async Task<IActionResult> OnGetExportAsync(CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(RunId))
        {
            return NotFound();
        }

        var export = await queryService.ExportAsync(RunId, Bucket, WorkerId, null, Filter, null, cancellationToken);
        if (export is null)
        {
            return NotFound();
        }

        var fileBytes = JsonSerializer.SerializeToUtf8Bytes(export, ExportSerializerOptions);
        return File(fileBytes, "application/json", BuildExportFileName());
    }

    private static string DescribePlannedAction(RunEntry entry, string? action, int operationCount)
    {
        var operationKinds = GetItemArray(entry.Item, "operations")
            .Select(operation => GetItemString(operation, "kind"))
            .Where(kind => !string.IsNullOrWhiteSpace(kind))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(kind => DescribeAction(kind!))
            .ToArray();
        if (operationKinds.Length > 0)
        {
            return string.Join(", ", operationKinds);
        }

        if (!string.IsNullOrWhiteSpace(action))
        {
            return DescribeAction(action);
        }

        return entry.Bucket switch
        {
            "creates" => "Create account",
            "updates" => operationCount > 0 ? "Update account" : "Account already up to date",
            "enables" => operationCount > 0 ? "Enable account" : "Keep account enabled",
            "disables" => operationCount > 0 ? "Disable account" : "Keep account disabled",
            "graveyardMoves" => operationCount > 0 ? "Move account to graveyard OU" : "Keep account in graveyard OU",
            "deletions" => "Delete account",
            "unchanged" => "No AD write required",
            "manualReview" => "No automatic AD change",
            "guardrailFailures" => "Blocked before AD change",
            "conflicts" => "AD change failed",
            _ => "No automatic AD change"
        };
    }

    private static string DescribeAction(string action) =>
        action switch
        {
            "CreateUser" => "Create account",
            "UpdateUser" => "Update account",
            "EnableUser" => "Enable account",
            "DisableUser" => "Disable account",
            "MoveUser" => "Move account",
            "DeleteUser" => "Delete account",
            _ => action
        };

    private static string FormatCount(int count, string noun)
        => count == 1 ? $"1 {noun}" : $"{count} {noun}s";

    private static string? GetItemString(JsonElement item, string propertyName)
    {
        if (item.ValueKind != JsonValueKind.Object || !item.TryGetProperty(propertyName, out var property))
        {
            return null;
        }

        return property.ValueKind == JsonValueKind.String
            ? property.GetString()
            : null;
    }

    private static bool? GetItemBoolean(JsonElement item, string propertyName)
    {
        if (item.ValueKind != JsonValueKind.Object || !item.TryGetProperty(propertyName, out var property))
        {
            return null;
        }

        return property.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => null
        };
    }

    private static int GetItemArrayCount(JsonElement item, string propertyName)
        => GetItemArray(item, propertyName).Count;

    private static IReadOnlyList<JsonElement> GetItemArray(JsonElement item, string propertyName)
    {
        if (item.ValueKind != JsonValueKind.Object || !item.TryGetProperty(propertyName, out var property) || property.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        return property.EnumerateArray().Select(element => element.Clone()).ToArray();
    }

    private static bool StringEquals(string? left, string? right)
        => string.Equals(left?.Trim(), right?.Trim(), StringComparison.OrdinalIgnoreCase);

    private string BuildExportFileName()
    {
        var segments = new List<string>
        {
            "syncfactors",
            "run",
            SanitizeFileNameSegment(RunId),
            string.IsNullOrWhiteSpace(Bucket) ? "all-buckets" : SanitizeFileNameSegment(Bucket)
        };

        if (!string.IsNullOrWhiteSpace(WorkerId))
        {
            segments.Add("worker-filtered");
        }

        if (!string.IsNullOrWhiteSpace(Filter))
        {
            segments.Add("text-filtered");
        }

        return $"{string.Join("-", segments)}-entries.json";
    }

    private static string SanitizeFileNameSegment(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "unknown";
        }

        var builder = new StringBuilder(value.Length);
        foreach (var character in value)
        {
            builder.Append(Path.GetInvalidFileNameChars().Contains(character) || char.IsWhiteSpace(character)
                ? '-'
                : char.ToLowerInvariant(character));
        }

        return builder.ToString().Trim('-');
    }

    public sealed record RunContextDisplay(
        string Label,
        string ToneCssClass,
        string Summary);

    public sealed record EntryExecutionDisplay(
        string Label,
        string ToneCssClass,
        string Summary,
        IReadOnlyList<EntryExecutionFact> Facts);

    public sealed record EntryExecutionFact(
        string Label,
        string Value);
}

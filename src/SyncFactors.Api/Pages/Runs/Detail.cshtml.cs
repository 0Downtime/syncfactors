using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using SyncFactors.Api;
using SyncFactors.Contracts;
using SyncFactors.Domain;
using System.Text.Json;

namespace SyncFactors.Api.Pages.Runs;

public sealed class DetailModel(RunEntriesQueryService queryService) : PageModel
{
    public const int EntriesPerPage = 15;

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

    private static bool StringEquals(string? left, string? right)
        => string.Equals(left?.Trim(), right?.Trim(), StringComparison.OrdinalIgnoreCase);
}

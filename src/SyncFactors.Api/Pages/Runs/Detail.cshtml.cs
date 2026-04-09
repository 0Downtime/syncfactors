using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using SyncFactors.Api;
using SyncFactors.Contracts;
using SyncFactors.Domain;

namespace SyncFactors.Api.Pages.Runs;

public sealed class DetailModel(RunEntriesQueryService queryService) : PageModel
{
    public const int EntriesPerPage = 50;

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

    public IReadOnlyList<string> AvailableBuckets { get; private set; } = [];

    public int TotalEntries { get; private set; }

    public int TotalPages { get; private set; }

    public bool HasPreviousPage => PageNumber > 1;

    public bool HasNextPage => PageNumber < TotalPages;

    public FailureDiagnostics? GetFailureDiagnostics(RunEntry entry)
        => ActiveDirectoryFailureDiagnostics.Parse(entry.Reason ?? entry.FailureSummary);

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
        Entries = result.Entries;
        PageNumber = result.Page;
        TotalPages = Math.Max(1, (int)Math.Ceiling(TotalEntries / (double)EntriesPerPage));
        return Page();
    }
}

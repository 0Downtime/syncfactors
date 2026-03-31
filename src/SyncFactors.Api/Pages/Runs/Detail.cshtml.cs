using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using SyncFactors.Contracts;
using SyncFactors.Domain;

namespace SyncFactors.Api.Pages.Runs;

public sealed class DetailModel(IRunRepository runRepository) : PageModel
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

    public IReadOnlyList<string> AvailableBuckets { get; private set; } = [];

    public int TotalEntries { get; private set; }

    public int TotalPages { get; private set; }

    public bool HasPreviousPage => PageNumber > 1;

    public bool HasNextPage => PageNumber < TotalPages;

    public async Task<IActionResult> OnGetAsync(CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(RunId))
        {
            return NotFound();
        }

        Run = await runRepository.GetRunAsync(RunId, cancellationToken);
        if (Run is null)
        {
            return NotFound();
        }

        AvailableBuckets = Run.BucketCounts
            .Where(pair => pair.Value > 0)
            .Select(pair => pair.Key)
            .ToArray();

        PageNumber = Math.Max(1, PageNumber);
        TotalEntries = await runRepository.CountRunEntriesAsync(RunId, Bucket, WorkerId, null, Filter, null, cancellationToken);
        TotalPages = Math.Max(1, (int)Math.Ceiling(TotalEntries / (double)EntriesPerPage));
        if (PageNumber > TotalPages)
        {
            PageNumber = TotalPages;
        }

        Entries = await runRepository.GetRunEntriesAsync(
            RunId,
            Bucket,
            WorkerId,
            null,
            Filter,
            null,
            (PageNumber - 1) * EntriesPerPage,
            EntriesPerPage,
            cancellationToken);
        return Page();
    }
}

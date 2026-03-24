using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using SyncFactors.Contracts;
using SyncFactors.Domain;

namespace SyncFactors.Api.Pages.Runs;

public sealed class DetailModel(IRunRepository runRepository) : PageModel
{
    [BindProperty(SupportsGet = true)]
    public string RunId { get; set; } = string.Empty;

    [BindProperty(SupportsGet = true)]
    public string? Bucket { get; set; }

    [BindProperty(SupportsGet = true)]
    public string? WorkerId { get; set; }

    [BindProperty(SupportsGet = true)]
    public string? Filter { get; set; }

    public RunDetail? Run { get; private set; }

    public IReadOnlyList<RunEntry> Entries { get; private set; } = [];

    public IReadOnlyList<string> AvailableBuckets { get; private set; } = [];

    public IReadOnlyList<string> AvailableWorkerIds { get; private set; } = [];

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

        var allEntries = await runRepository.GetRunEntriesAsync(RunId, null, null, null, null, null, cancellationToken);
        AvailableWorkerIds = allEntries
            .Select(entry => entry.WorkerId)
            .Where(workerId => !string.IsNullOrWhiteSpace(workerId))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(workerId => workerId, StringComparer.Ordinal)
            .Cast<string>()
            .ToArray();

        Entries = await runRepository.GetRunEntriesAsync(RunId, Bucket, WorkerId, null, Filter, null, cancellationToken);
        return Page();
    }
}

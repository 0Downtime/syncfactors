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

    public RunDetail? Run { get; private set; }

    public IReadOnlyList<RunEntry> Entries { get; private set; } = [];

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

        Entries = await runRepository.GetRunEntriesAsync(RunId, Bucket, null, null, null, null, cancellationToken);
        return Page();
    }
}

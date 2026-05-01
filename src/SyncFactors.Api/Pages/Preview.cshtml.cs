using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using SyncFactors.Domain;

namespace SyncFactors.Api.Pages;

public sealed class PreviewModel(IRunRepository runRepository) : PageModel
{
    [BindProperty(SupportsGet = true, Name = "runId")]
    public string? SavedRunId { get; set; }

    [BindProperty(SupportsGet = true)]
    public string? WorkerId { get; set; }

    [BindProperty(SupportsGet = true)]
    public bool ShowAllAttributes { get; set; }

    public async Task<IActionResult> OnGetAsync(CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(WorkerId))
        {
            return RedirectToPage("/Workers", new
            {
                workerId = WorkerId,
                showAllAttributes = ShowAllAttributes
            });
        }

        if (!string.IsNullOrWhiteSpace(SavedRunId))
        {
            var preview = await runRepository.GetWorkerPreviewAsync(SavedRunId, cancellationToken);
            if (preview is not null)
            {
                return RedirectToPage("/Workers", new
                {
                    workerId = preview.WorkerId,
                    runId = SavedRunId,
                    showAllAttributes = ShowAllAttributes
                });
            }

            return RedirectToPage("/Workers", new
            {
                runId = SavedRunId,
                showAllAttributes = ShowAllAttributes
            });
        }

        return RedirectToPage("/Workers");
    }
}

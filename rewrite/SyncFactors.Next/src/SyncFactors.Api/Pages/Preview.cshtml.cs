using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using SyncFactors.Contracts;
using SyncFactors.Domain;

namespace SyncFactors.Api.Pages;

public sealed class PreviewModel(
    IWorkerPreviewPlanner previewPlanner,
    IApplyPreviewService applyPreviewService) : PageModel
{
    [BindProperty(SupportsGet = true)]
    public string WorkerId { get; set; } = string.Empty;

    public WorkerPreviewResult? Preview { get; private set; }

    public DirectoryCommandResult? ApplyResult { get; private set; }

    public string? ErrorMessage { get; private set; }

    public async Task OnGetAsync(CancellationToken cancellationToken)
    {
        await LoadPreviewAsync(cancellationToken);
    }

    public async Task<IActionResult> OnPostApplyAsync(CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(WorkerId))
        {
            ErrorMessage = "Worker ID is required.";
            return Page();
        }

        try
        {
            ApplyResult = await applyPreviewService.ApplyAsync(WorkerId, cancellationToken);
        }
        catch (Exception ex)
        {
            ErrorMessage = ex.Message;
        }

        await LoadPreviewAsync(cancellationToken);
        return Page();
    }

    private async Task LoadPreviewAsync(CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(WorkerId))
        {
            return;
        }

        try
        {
            Preview = await previewPlanner.PreviewAsync(WorkerId, cancellationToken);
        }
        catch (Exception ex)
        {
            ErrorMessage = ex.Message;
        }
    }
}

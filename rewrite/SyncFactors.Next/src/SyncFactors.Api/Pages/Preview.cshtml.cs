using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using SyncFactors.Contracts;
using SyncFactors.Domain;

namespace SyncFactors.Api.Pages;

public sealed class PreviewModel(IWorkerPreviewPlanner previewPlanner) : PageModel
{
    [BindProperty(SupportsGet = true)]
    public string WorkerId { get; set; } = string.Empty;

    public WorkerPreviewResult? Preview { get; private set; }

    public string? ErrorMessage { get; private set; }

    public async Task OnGetAsync(CancellationToken cancellationToken)
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

using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using SyncFactors.Contracts;
using SyncFactors.Infrastructure;

namespace SyncFactors.Api.Pages;

public sealed class PreviewModel(PowerShellWorkerPreviewService previewService) : PageModel
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
            Preview = await previewService.PreviewAsync(WorkerId, cancellationToken);
        }
        catch (Exception ex)
        {
            ErrorMessage = ex.Message;
        }
    }
}

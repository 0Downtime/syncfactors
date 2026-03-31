using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using SyncFactors.Contracts;
using SyncFactors.Domain;

namespace SyncFactors.Api.Pages;

public sealed class PreviewModel(
    IWorkerPreviewPlanner previewPlanner,
    IApplyPreviewService applyPreviewService,
    IRunRepository runRepository) : PageModel
{
    [BindProperty(SupportsGet = true, Name = "runId")]
    public string SavedRunId { get; set; } = string.Empty;

    [BindProperty(SupportsGet = true)]
    public string WorkerId { get; set; } = string.Empty;

    [BindProperty(SupportsGet = true)]
    public bool ShowAllAttributes { get; set; }

    [BindProperty]
    public bool AcknowledgeRealSync { get; set; }

    [BindProperty]
    public string PreviewRunId { get; set; } = string.Empty;

    [BindProperty]
    public string PreviewFingerprint { get; set; } = string.Empty;

    public WorkerPreviewResult? Preview { get; private set; }

    public DirectoryCommandResult? ApplyResult { get; private set; }

    public string? ErrorMessage { get; private set; }

    public WorkerPreviewResult? PreviousPreview { get; private set; }

    public IReadOnlyList<WorkerPreviewHistoryItem> PreviewHistory { get; private set; } = [];

    public IReadOnlyList<DiffRow> VisibleDiffRows =>
        Preview is null
            ? []
            : (ShowAllAttributes ? Preview.DiffRows : Preview.DiffRows.Where(row => row.Changed).ToArray());

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

        if (string.IsNullOrWhiteSpace(PreviewRunId) || string.IsNullOrWhiteSpace(PreviewFingerprint))
        {
            ErrorMessage = "Refresh preview before applying.";
            await LoadPreviewAsync(cancellationToken);
            return Page();
        }

        try
        {
            ApplyResult = await applyPreviewService.ApplyAsync(
                new ApplyPreviewRequest(
                    WorkerId: WorkerId,
                    PreviewRunId: PreviewRunId,
                    PreviewFingerprint: PreviewFingerprint,
                    AcknowledgeRealSync: AcknowledgeRealSync),
                cancellationToken);
        }
        catch (Exception ex)
        {
            ErrorMessage = ex.Message;
        }

        SavedRunId = PreviewRunId;
        await LoadPreviewAsync(cancellationToken);
        return Page();
    }

    private async Task LoadPreviewAsync(CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(SavedRunId))
        {
            Preview = await runRepository.GetWorkerPreviewAsync(SavedRunId, cancellationToken);
            if (Preview is null)
            {
                ErrorMessage = $"Preview run {SavedRunId} could not be resolved.";
                return;
            }

            WorkerId = Preview.WorkerId;
            PreviewRunId = Preview.RunId ?? string.Empty;
            PreviewFingerprint = Preview.Fingerprint;
            PreviewHistory = await runRepository.ListWorkerPreviewHistoryAsync(WorkerId, 6, cancellationToken);
            if (!string.IsNullOrWhiteSpace(Preview.PreviousRunId))
            {
                PreviousPreview = await runRepository.GetWorkerPreviewAsync(Preview.PreviousRunId, cancellationToken);
            }

            return;
        }

        if (string.IsNullOrWhiteSpace(WorkerId))
        {
            return;
        }

        try
        {
            Preview = await previewPlanner.PreviewAsync(WorkerId, cancellationToken);
            PreviewRunId = Preview.RunId ?? string.Empty;
            PreviewFingerprint = Preview.Fingerprint;
            SavedRunId = PreviewRunId;
            PreviewHistory = await runRepository.ListWorkerPreviewHistoryAsync(WorkerId, 6, cancellationToken);
            if (!string.IsNullOrWhiteSpace(Preview.PreviousRunId))
            {
                PreviousPreview = await runRepository.GetWorkerPreviewAsync(Preview.PreviousRunId, cancellationToken);
            }
        }
        catch (Exception ex)
        {
            ErrorMessage = ex.Message;
        }
    }
}

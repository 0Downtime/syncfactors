using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using SyncFactors.Contracts;
using SyncFactors.Domain;

namespace SyncFactors.Api.Pages;

public sealed class IndexModel(
    IDashboardSnapshotService dashboardSnapshotService,
    IFullSyncRunService fullSyncRunService) : PageModel
{
    public RuntimeStatus Status { get; private set; } = new(
        Status: "Idle",
        Stage: "NotStarted",
        RunId: null,
        Mode: null,
        DryRun: true,
        ProcessedWorkers: 0,
        TotalWorkers: 0,
        CurrentWorkerId: null,
        LastAction: null,
        StartedAt: null,
        LastUpdatedAt: null,
        CompletedAt: null,
        ErrorMessage: null);

    public IReadOnlyList<RunSummary> Runs { get; private set; } = [];

    public RunSummary? ActiveRun { get; private set; }

    public RunSummary? LastCompletedRun { get; private set; }

    public bool RequiresAttention { get; private set; }

    public string? AttentionMessage { get; private set; }

    public bool CanLaunchSync => !string.Equals(Status.Status, "InProgress", StringComparison.OrdinalIgnoreCase);

    [BindProperty]
    public bool AcknowledgeRealSync { get; set; }

    [TempData]
    public string? LaunchRunId { get; set; }

    [TempData]
    public string? LaunchMessage { get; set; }

    [TempData]
    public bool LaunchFailed { get; set; }

    public async Task OnGetAsync(CancellationToken cancellationToken)
    {
        await LoadSnapshotAsync(cancellationToken);
    }

    public async Task<IActionResult> OnPostDryRunAsync(CancellationToken cancellationToken)
    {
        try
        {
            var result = await fullSyncRunService.LaunchAsync(
                new LaunchFullRunRequest(DryRun: true, AcknowledgeRealSync: false),
                cancellationToken);
            LaunchRunId = result.RunId;
            LaunchMessage = result.Message;
            LaunchFailed = !string.Equals(result.Status, "Succeeded", StringComparison.OrdinalIgnoreCase);
            return RedirectToPage();
        }
        catch (Exception ex)
        {
            LaunchFailed = true;
            LaunchMessage = ex.Message;
            await LoadSnapshotAsync(cancellationToken);
            return Page();
        }
    }

    public async Task<IActionResult> OnPostLiveRunAsync(CancellationToken cancellationToken)
    {
        try
        {
            var result = await fullSyncRunService.LaunchAsync(
                new LaunchFullRunRequest(DryRun: false, AcknowledgeRealSync: AcknowledgeRealSync),
                cancellationToken);
            LaunchRunId = result.RunId;
            LaunchMessage = result.Message;
            LaunchFailed = !string.Equals(result.Status, "Succeeded", StringComparison.OrdinalIgnoreCase);
            return RedirectToPage();
        }
        catch (Exception ex)
        {
            LaunchFailed = true;
            LaunchMessage = ex.Message;
            await LoadSnapshotAsync(cancellationToken);
            return Page();
        }
    }

    private async Task LoadSnapshotAsync(CancellationToken cancellationToken)
    {
        var snapshot = await dashboardSnapshotService.GetSnapshotAsync(cancellationToken);
        Status = snapshot.Status;
        Runs = snapshot.Runs;
        ActiveRun = snapshot.ActiveRun;
        LastCompletedRun = snapshot.LastCompletedRun;
        RequiresAttention = snapshot.RequiresAttention;
        AttentionMessage = snapshot.AttentionMessage;
    }
}

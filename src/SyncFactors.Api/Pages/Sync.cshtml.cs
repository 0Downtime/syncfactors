using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using SyncFactors.Contracts;
using SyncFactors.Domain;

namespace SyncFactors.Api.Pages;

public sealed class SyncModel(
    IDashboardSnapshotService dashboardSnapshotService,
    IRunQueueStore runQueueStore,
    ISyncScheduleStore syncScheduleStore,
    IFullSyncRunService fullSyncRunService) : PageModel
{
    private const string DryRunMode = "DryRun";
    private const string LiveRunMode = "LiveRun";

    [BindProperty]
    public string RunMode { get; set; } = DryRunMode;

    [BindProperty]
    public bool ScheduleEnabled { get; set; }

    [BindProperty]
    public int IntervalMinutes { get; set; } = 30;

    [BindProperty]
    public bool AcknowledgeRealSync { get; set; }

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

    public SyncScheduleStatus Schedule { get; private set; } = new(
        Enabled: false,
        IntervalMinutes: 30,
        NextRunAt: null,
        LastScheduledRunAt: null,
        LastEnqueueAttemptAt: null,
        LastEnqueueError: null);

    public IReadOnlyList<RunSummary> Runs { get; private set; } = [];

    public RunSummary? ActiveRun { get; private set; }

    public bool HasPendingOrActiveRun { get; private set; }

    public bool CanLaunchSync => !string.Equals(Status.Status, "InProgress", StringComparison.OrdinalIgnoreCase);

    [TempData]
    public string? ErrorMessage { get; set; }

    [TempData]
    public string? SuccessMessage { get; set; }

    [TempData]
    public string? LaunchRunId { get; set; }

    public async Task OnGetAsync(CancellationToken cancellationToken)
    {
        await LoadAsync(cancellationToken);
    }

    public async Task<IActionResult> OnPostDryRunAsync(CancellationToken cancellationToken)
    {
        try
        {
            var result = await fullSyncRunService.LaunchAsync(
                new LaunchFullRunRequest(DryRun: true, AcknowledgeRealSync: false),
                cancellationToken);

            LaunchRunId = result.RunId;
            SuccessMessage = result.Message;
            ErrorMessage = null;
            return RedirectToPage();
        }
        catch (Exception ex)
        {
            ErrorMessage = ex.Message;
            SuccessMessage = null;
            LaunchRunId = null;
            await LoadAsync(cancellationToken);
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
            SuccessMessage = result.Message;
            ErrorMessage = null;
            return RedirectToPage();
        }
        catch (Exception ex)
        {
            ErrorMessage = ex.Message;
            SuccessMessage = null;
            LaunchRunId = null;
            await LoadAsync(cancellationToken);
            return Page();
        }
    }

    public async Task<IActionResult> OnPostStartRunAsync(CancellationToken cancellationToken)
    {
        if (await runQueueStore.HasPendingOrActiveRunAsync(cancellationToken))
        {
            ErrorMessage = "A run is already pending or in progress.";
            await LoadAsync(cancellationToken);
            return Page();
        }

        await runQueueStore.EnqueueAsync(
            new StartRunRequest(
                DryRun: !string.Equals(RunMode, LiveRunMode, StringComparison.Ordinal),
                RunTrigger: "AdHoc",
                RequestedBy: "Sync page"),
            cancellationToken);

        SuccessMessage = string.Equals(RunMode, LiveRunMode, StringComparison.Ordinal)
            ? "Live provisioning run queued."
            : "Dry-run sync queued.";

        await LoadAsync(cancellationToken);
        return Page();
    }

    public async Task<IActionResult> OnPostSaveScheduleAsync(CancellationToken cancellationToken)
    {
        Schedule = await syncScheduleStore.UpdateAsync(
            new UpdateSyncScheduleRequest(
                Enabled: ScheduleEnabled,
                IntervalMinutes: IntervalMinutes),
            cancellationToken);

        SuccessMessage = Schedule.Enabled
            ? $"Recurring sync enabled every {Schedule.IntervalMinutes} minutes."
            : "Recurring sync disabled.";

        await LoadSnapshotAsync(cancellationToken);
        HasPendingOrActiveRun = await runQueueStore.HasPendingOrActiveRunAsync(cancellationToken);
        return Page();
    }

    private async Task LoadAsync(CancellationToken cancellationToken)
    {
        await LoadSnapshotAsync(cancellationToken);
        Schedule = await syncScheduleStore.GetCurrentAsync(cancellationToken);
        ScheduleEnabled = Schedule.Enabled;
        IntervalMinutes = Schedule.IntervalMinutes;
        HasPendingOrActiveRun = await runQueueStore.HasPendingOrActiveRunAsync(cancellationToken);
    }

    private async Task LoadSnapshotAsync(CancellationToken cancellationToken)
    {
        var snapshot = await dashboardSnapshotService.GetSnapshotAsync(cancellationToken);
        Status = snapshot.Status;
        Runs = snapshot.Runs;
        ActiveRun = snapshot.ActiveRun;
    }
}

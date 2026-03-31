using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using SyncFactors.Contracts;
using SyncFactors.Domain;

namespace SyncFactors.Api.Pages;

public sealed class SyncModel(
    IDashboardSnapshotService dashboardSnapshotService,
    IRunQueueStore runQueueStore,
    ISyncScheduleStore syncScheduleStore) : PageModel
{
    private const string DryRunMode = "DryRun";
    private const string LiveRunMode = "LiveRun";

    [BindProperty]
    public string RunMode { get; set; } = DryRunMode;

    [BindProperty]
    public bool ScheduleEnabled { get; set; }

    [BindProperty]
    public int IntervalMinutes { get; set; } = 30;

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

    public string? ErrorMessage { get; private set; }

    public string? SuccessMessage { get; private set; }

    public async Task OnGetAsync(CancellationToken cancellationToken)
    {
        await LoadAsync(cancellationToken);
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

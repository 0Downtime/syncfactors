using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using SyncFactors.Contracts;
using SyncFactors.Domain;

namespace SyncFactors.Api.Pages;

public sealed class SyncModel(
    IDashboardSnapshotService dashboardSnapshotService,
    IRunQueueStore runQueueStore,
    RealSyncSettings realSyncSettings,
    ISyncScheduleStore syncScheduleStore) : PageModel
{
    private const int RunsPageSize = 25;
    private const string DryRunMode = "DryRun";
    private const string LiveRunMode = "LiveRun";
    private const string DeleteAllUsersMode = "DeleteAllUsers";
    public const string DeleteAllUsersConfirmationPhrase = "DELETE ALL USERS";

    [BindProperty]
    public string RunMode { get; set; } = DryRunMode;

    [BindProperty]
    public bool ScheduleEnabled { get; set; }

    [BindProperty]
    public int IntervalMinutes { get; set; } = 30;

    [BindProperty]
    public bool AcknowledgeRealSync { get; set; }

    [BindProperty]
    public string DeleteAllUsersConfirmationText { get; set; } = string.Empty;

    [BindProperty(SupportsGet = true)]
    public int PageNumber { get; set; } = 1;

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

    public int TotalRunsCount { get; private set; }

    public int TotalRunPages => Math.Max(1, (int)Math.Ceiling(TotalRunsCount / (double)RunsPageSize));

    public bool HasPreviousRunPage => PageNumber > 1;

    public bool HasNextRunPage => PageNumber < TotalRunPages;

    public RunSummary? ActiveRun { get; private set; }

    public RunQueueRequest? CurrentQueueRequest { get; private set; }

    public bool HasPendingOrActiveRun { get; private set; }

    public bool CanLaunchSync => !string.Equals(Status.Status, "InProgress", StringComparison.OrdinalIgnoreCase);

    public bool RealSyncEnabled => realSyncSettings.Enabled;

    public bool ScheduledRunsAreDryRunOnly => !realSyncSettings.Enabled;

    [TempData]
    public string? ErrorMessage { get; set; }

    [TempData]
    public string? SuccessMessage { get; set; }

    public async Task OnGetAsync(CancellationToken cancellationToken)
    {
        await LoadAsync(cancellationToken);
    }

    public async Task<IActionResult> OnPostStartRunAsync(CancellationToken cancellationToken)
    {
        if (await runQueueStore.HasPendingOrActiveRunAsync(cancellationToken))
        {
            ErrorMessage = "A run is already pending or in progress.";
            SuccessMessage = null;
            return RedirectToPage(new { PageNumber });
        }

        if (string.Equals(RunMode, LiveRunMode, StringComparison.Ordinal) && !realSyncSettings.Enabled)
        {
            ErrorMessage = "Live provisioning is disabled for this environment. Queue a dry run instead.";
            SuccessMessage = null;
            return RedirectToPage(new { PageNumber });
        }

        await runQueueStore.EnqueueAsync(
            new StartRunRequest(
                DryRun: !string.Equals(RunMode, LiveRunMode, StringComparison.Ordinal),
                Mode: "BulkSync",
                RunTrigger: "AdHoc",
                RequestedBy: ResolveRequestedBy()),
            cancellationToken);

        SuccessMessage = string.Equals(RunMode, LiveRunMode, StringComparison.Ordinal)
            ? "Live provisioning run queued."
            : "Dry-run sync queued.";
        ErrorMessage = null;
        return RedirectToPage(new { PageNumber });
    }

    public async Task<IActionResult> OnPostDeleteAllUsersAsync(CancellationToken cancellationToken)
    {
        if (await runQueueStore.HasPendingOrActiveRunAsync(cancellationToken))
        {
            ErrorMessage = "A run is already pending or in progress.";
            SuccessMessage = null;
            return RedirectToPage(new { PageNumber });
        }

        if (!realSyncSettings.Enabled)
        {
            ErrorMessage = "Real AD sync is disabled for this environment.";
            SuccessMessage = null;
            return RedirectToPage(new { PageNumber });
        }

        if (!string.Equals(DeleteAllUsersConfirmationText?.Trim(), DeleteAllUsersConfirmationPhrase, StringComparison.Ordinal))
        {
            ErrorMessage = $"Type {DeleteAllUsersConfirmationPhrase} to queue the delete-all AD reset run.";
            SuccessMessage = null;
            return RedirectToPage(new { PageNumber });
        }

        await runQueueStore.EnqueueAsync(
            new StartRunRequest(
                DryRun: false,
                Mode: DeleteAllUsersMode,
                RunTrigger: "DeleteAllUsers",
                RequestedBy: ResolveRequestedBy()),
            cancellationToken);

        SuccessMessage = "Delete-all AD reset queued.";
        ErrorMessage = null;
        return RedirectToPage(new { PageNumber });
    }

    public async Task<IActionResult> OnPostCancelRunAsync(CancellationToken cancellationToken)
    {
        if (!await runQueueStore.CancelPendingOrActiveAsync(ResolveRequestedBy(), cancellationToken))
        {
            ErrorMessage = "No queued or active run was available to cancel.";
            SuccessMessage = null;
            return RedirectToPage(new { PageNumber });
        }

        SuccessMessage = "Run cancellation requested.";
        ErrorMessage = null;
        return RedirectToPage(new { PageNumber });
    }

    public async Task<IActionResult> OnPostSaveScheduleAsync(CancellationToken cancellationToken)
    {
        Schedule = await syncScheduleStore.UpdateAsync(
            new UpdateSyncScheduleRequest(
                Enabled: ScheduleEnabled,
                IntervalMinutes: IntervalMinutes),
            cancellationToken);

        SuccessMessage = Schedule.Enabled
            ? ScheduledRunsAreDryRunOnly
                ? $"Recurring dry-run sync enabled every {Schedule.IntervalMinutes} minutes."
                : $"Recurring sync enabled every {Schedule.IntervalMinutes} minutes."
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
        CurrentQueueRequest = await runQueueStore.GetPendingOrActiveAsync(cancellationToken);
        HasPendingOrActiveRun = CurrentQueueRequest is not null;
    }

    private async Task LoadSnapshotAsync(CancellationToken cancellationToken)
    {
        var snapshot = await dashboardSnapshotService.GetSnapshotAsync(cancellationToken);
        Status = snapshot.Status;
        TotalRunsCount = snapshot.Runs.Count;
        PageNumber = Math.Clamp(PageNumber, 1, TotalRunPages);
        Runs = snapshot.Runs
            .Skip((PageNumber - 1) * RunsPageSize)
            .Take(RunsPageSize)
            .ToArray();
        ActiveRun = snapshot.ActiveRun;
    }

    private string ResolveRequestedBy() =>
        string.IsNullOrWhiteSpace(PageContext?.HttpContext?.User.Identity?.Name)
            ? "Sync page"
            : PageContext.HttpContext.User.Identity!.Name!;
}

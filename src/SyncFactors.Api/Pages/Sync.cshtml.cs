using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.Hosting;
using SyncFactors.Contracts;
using SyncFactors.Domain;
using SyncFactors.Infrastructure;

namespace SyncFactors.Api.Pages;

public sealed class SyncModel(
    IDashboardSnapshotService dashboardSnapshotService,
    IRunQueueStore runQueueStore,
    RealSyncSettings realSyncSettings,
    ISyncScheduleStore syncScheduleStore,
    IWebHostEnvironment hostEnvironment,
    ISecurityAuditService? audit = null) : PageModel
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

    public bool RealSyncEnabled => realSyncSettings.EffectiveWriteEnabled;

    public bool DryRunOnlyMode => realSyncSettings.DryRunOnly;

    public string LiveWriteDisabledMessage => realSyncSettings.LiveWriteDisabledMessage;

    public bool ScheduledRunsAreDryRunOnly => realSyncSettings.RequiresDryRun;

    public bool CanManageSchedule =>
        User.IsInRole(SecurityRoles.Admin) ||
        User.IsInRole(SecurityRoles.BreakGlassAdmin);

    public bool CanQueueDeleteAllUsers => false;

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
        if (string.Equals(RunMode, LiveRunMode, StringComparison.Ordinal) && !realSyncSettings.EffectiveWriteEnabled)
        {
            ErrorMessage = $"{realSyncSettings.LiveWriteDisabledMessage} Queue a dry run instead.";
            SuccessMessage = null;
            return RedirectToPage(new { PageNumber });
        }

        RunQueueRequest queued;
        try
        {
            queued = await runQueueStore.EnqueueAsync(
                new StartRunRequest(
                    DryRun: !string.Equals(RunMode, LiveRunMode, StringComparison.Ordinal),
                    Mode: "BulkSync",
                    RunTrigger: "AdHoc",
                    RequestedBy: ResolveRequestedBy()),
                cancellationToken);
        }
        catch (RunQueueConflictException)
        {
            ErrorMessage = "A run is already pending or in progress.";
            SuccessMessage = null;
            return RedirectToPage(new { PageNumber });
        }

        SuccessMessage = string.Equals(RunMode, LiveRunMode, StringComparison.Ordinal)
            ? "Live provisioning run queued."
            : "Dry-run sync queued.";
        ErrorMessage = null;
        TryWriteAudit(() => audit?.Write("RunQueued", "Success", ("RequestedBy", queued.RequestedBy), ("Mode", queued.Mode), ("DryRun", queued.DryRun)));
        return RedirectToPage("/Index");
    }

    public async Task<IActionResult> OnPostDeleteAllUsersAsync(CancellationToken cancellationToken)
    {
        if (!hostEnvironment.IsDevelopment() ||
            !User.IsInRole(SecurityRoles.Admin) && !User.IsInRole(SecurityRoles.BreakGlassAdmin))
        {
            return Forbid();
        }

        ErrorMessage = RunQueueProtocol.DeletionCapabilityDisabledMessage;
        SuccessMessage = null;
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
        TryWriteAudit(() => audit?.Write("RunCancelled", "Success", ("RequestedBy", ResolveRequestedBy())));
        return RedirectToPage(new { PageNumber });
    }

    public async Task<IActionResult> OnPostSaveScheduleAsync(CancellationToken cancellationToken)
    {
        if (!CanManageSchedule)
        {
            return Forbid();
        }

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
        ErrorMessage = null;
        TryWriteAudit(() => audit?.Write("SyncScheduleUpdated", "Success", ("RequestedBy", ResolveRequestedBy()), ("Enabled", Schedule.Enabled), ("IntervalMinutes", Schedule.IntervalMinutes)));

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

    private void TryWriteAudit(Action write)
    {
        try
        {
            write();
        }
        catch (Exception)
        {
            ErrorMessage = "The action completed, but security audit recording failed.";
        }
    }
}

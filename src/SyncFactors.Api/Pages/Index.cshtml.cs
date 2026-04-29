using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using SyncFactors.Contracts;
using SyncFactors.Domain;
using SyncFactors.Infrastructure;

namespace SyncFactors.Api.Pages;

public sealed class IndexModel(
    IDashboardSnapshotService dashboardSnapshotService,
    DashboardSettingsProvider dashboardSettingsProvider,
    ISyncScheduleStore syncScheduleStore,
    IRunQueueStore runQueueStore,
    IWebHostEnvironment hostEnvironment) : PageModel
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

    public bool HealthProbesEnabled { get; private set; }

    public bool DefaultHealthProbesEnabled { get; private set; }

    public bool HealthProbesUseOverride { get; private set; }

    public int HealthProbeIntervalSeconds { get; private set; }

    public int DefaultHealthProbeIntervalSeconds { get; private set; }

    public RunQueueRequest? CurrentQueueRequest { get; private set; }

    public bool HasPendingOrActiveRun => CurrentQueueRequest is not null;

    public bool CancellationRequested =>
        string.Equals(CurrentQueueRequest?.Status, "CancelRequested", StringComparison.OrdinalIgnoreCase);

    public bool IsDevelopment { get; } = hostEnvironment.IsDevelopment();

    [TempData]
    public string? ErrorMessage { get; set; }

    [TempData]
    public string? SuccessMessage { get; set; }

    public SyncScheduleStatus Schedule { get; private set; } = new(
        Enabled: false,
        IntervalMinutes: 30,
        NextRunAt: null,
        LastScheduledRunAt: null,
        LastEnqueueAttemptAt: null,
        LastEnqueueError: null);

    public async Task OnGetAsync(CancellationToken cancellationToken)
    {
        await LoadSnapshotAsync(cancellationToken);
    }

    public async Task<IActionResult> OnPostSetHealthProbesAsync(bool enabled, CancellationToken cancellationToken)
    {
        if (!IsDevelopment || !User.IsInRole("Admin") && !User.IsInRole("BreakGlassAdmin"))
        {
            return Forbid();
        }

        var settings = await dashboardSettingsProvider.SetHealthProbesEnabledAsync(enabled, cancellationToken);
        HealthProbesEnabled = settings.Enabled;
        DefaultHealthProbesEnabled = settings.DefaultEnabled;
        HealthProbesUseOverride = settings.IsOverride;
        SuccessMessage = settings.Enabled
            ? "Dashboard health probes enabled."
            : "Dashboard health probes disabled.";
        ErrorMessage = null;
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostSetHealthProbeFrequencyAsync(int intervalSeconds, CancellationToken cancellationToken)
    {
        if (!IsDevelopment || !User.IsInRole("Admin") && !User.IsInRole("BreakGlassAdmin"))
        {
            return Forbid();
        }

        var settings = await dashboardSettingsProvider.SetHealthProbeIntervalSecondsAsync(intervalSeconds, cancellationToken);
        HealthProbesEnabled = settings.Enabled;
        DefaultHealthProbesEnabled = settings.DefaultEnabled;
        HealthProbesUseOverride = settings.IsOverride;
        HealthProbeIntervalSeconds = settings.IntervalSeconds;
        DefaultHealthProbeIntervalSeconds = settings.DefaultIntervalSeconds;
        SuccessMessage = $"Dashboard health probe frequency set to every {settings.IntervalSeconds} seconds.";
        ErrorMessage = null;
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostCancelRunAsync(CancellationToken cancellationToken)
    {
        if (!CanOperate())
        {
            return Forbid();
        }

        if (!await runQueueStore.CancelPendingOrActiveAsync(ResolveRequestedBy(), cancellationToken))
        {
            ErrorMessage = "No queued or active run was available to cancel.";
            SuccessMessage = null;
            return RedirectToPage();
        }

        SuccessMessage = "Run cancellation requested.";
        ErrorMessage = null;
        return RedirectToPage();
    }

    private async Task LoadSnapshotAsync(CancellationToken cancellationToken)
    {
        var snapshot = await dashboardSnapshotService.GetSnapshotAsync(cancellationToken);
        var settings = await dashboardSettingsProvider.GetHealthProbeStateAsync(cancellationToken);
        Schedule = await syncScheduleStore.GetCurrentAsync(cancellationToken);
        CurrentQueueRequest = await runQueueStore.GetPendingOrActiveAsync(cancellationToken);
        Status = snapshot.Status;
        Runs = snapshot.Runs;
        ActiveRun = snapshot.ActiveRun;
        LastCompletedRun = snapshot.LastCompletedRun;
        RequiresAttention = snapshot.RequiresAttention;
        AttentionMessage = snapshot.AttentionMessage;
        HealthProbesEnabled = settings.Enabled;
        DefaultHealthProbesEnabled = settings.DefaultEnabled;
        HealthProbesUseOverride = settings.IsOverride;
        HealthProbeIntervalSeconds = settings.IntervalSeconds;
        DefaultHealthProbeIntervalSeconds = settings.DefaultIntervalSeconds;
    }

    private string ResolveRequestedBy() =>
        string.IsNullOrWhiteSpace(PageContext?.HttpContext?.User.Identity?.Name)
            ? "Dashboard"
            : PageContext.HttpContext.User.Identity!.Name!;

    private bool CanOperate() =>
        User.IsInRole(SecurityRoles.Operator) ||
        User.IsInRole(SecurityRoles.Admin) ||
        User.IsInRole(SecurityRoles.BreakGlassAdmin);
}

using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using SyncFactors.Contracts;
using SyncFactors.Domain;

namespace SyncFactors.Api.Pages;

public sealed class IndexModel(
    IDashboardSnapshotService dashboardSnapshotService,
    DashboardOptions dashboardOptions,
    ISyncScheduleStore syncScheduleStore) : PageModel
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

    public bool HealthProbesEnabled { get; } = dashboardOptions.HealthProbesEnabled;

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

    private async Task LoadSnapshotAsync(CancellationToken cancellationToken)
    {
        var snapshot = await dashboardSnapshotService.GetSnapshotAsync(cancellationToken);
        Schedule = await syncScheduleStore.GetCurrentAsync(cancellationToken);
        Status = snapshot.Status;
        Runs = snapshot.Runs;
        ActiveRun = snapshot.ActiveRun;
        LastCompletedRun = snapshot.LastCompletedRun;
        RequiresAttention = snapshot.RequiresAttention;
        AttentionMessage = snapshot.AttentionMessage;
    }
}

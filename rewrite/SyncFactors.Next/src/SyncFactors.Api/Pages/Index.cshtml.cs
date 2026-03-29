using Microsoft.AspNetCore.Mvc.RazorPages;
using SyncFactors.Contracts;
using SyncFactors.Domain;

namespace SyncFactors.Api.Pages;

public sealed class IndexModel(IDashboardSnapshotService dashboardSnapshotService) : PageModel
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

    public async Task OnGetAsync(CancellationToken cancellationToken)
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

using Microsoft.AspNetCore.Mvc.RazorPages;
using SyncFactors.Contracts;
using SyncFactors.Domain;

namespace SyncFactors.Api.Pages;

public sealed class IndexModel(IRuntimeStatusStore runtimeStatusStore, IRunRepository runRepository) : PageModel
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

    public async Task OnGetAsync(CancellationToken cancellationToken)
    {
        Status = await runtimeStatusStore.GetCurrentAsync(cancellationToken) ?? Status;
        Runs = await runRepository.ListRunsAsync(cancellationToken);
    }
}

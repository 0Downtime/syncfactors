using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using SyncFactors.Domain;

namespace SyncFactors.Api.Pages.Admin;

[Authorize(Roles = "Admin,BreakGlassAdmin")]
public sealed class DeletionQueueModel(
    GraveyardDeletionQueueService deletionQueueService,
    IGraveyardRetentionStore retentionStore,
    TimeProvider timeProvider) : PageModel
{
    public IReadOnlyList<GraveyardDeletionQueueItem> PendingUsers { get; private set; } = [];

    public IReadOnlyList<GraveyardDeletionQueueItem> HeldUsers { get; private set; } = [];

    [TempData]
    public string? ErrorMessage { get; set; }

    [TempData]
    public string? SuccessMessage { get; set; }

    public async Task OnGetAsync(CancellationToken cancellationToken)
    {
        await LoadAsync(cancellationToken);
    }

    public async Task<IActionResult> OnPostPlaceHoldAsync(string workerId, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(workerId))
        {
            ErrorMessage = "Worker ID is required to place a hold.";
            SuccessMessage = null;
            return RedirectToPage();
        }

        await retentionStore.SetHoldAsync(
            workerId,
            isOnHold: true,
            actingUserId: GetActingUserId(),
            changedAtUtc: timeProvider.GetUtcNow(),
            cancellationToken);
        SuccessMessage = $"Placed a deletion hold for worker {workerId}.";
        ErrorMessage = null;
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostRemoveHoldAsync(string workerId, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(workerId))
        {
            ErrorMessage = "Worker ID is required to remove a hold.";
            SuccessMessage = null;
            return RedirectToPage();
        }

        await retentionStore.SetHoldAsync(
            workerId,
            isOnHold: false,
            actingUserId: GetActingUserId(),
            changedAtUtc: timeProvider.GetUtcNow(),
            cancellationToken);
        SuccessMessage = $"Removed the deletion hold for worker {workerId}.";
        ErrorMessage = null;
        return RedirectToPage();
    }

    public string FormatCountdown(GraveyardDeletionQueueItem item) =>
        item.OverdueDays > 0
            ? $"Overdue by {item.OverdueDays} day{(item.OverdueDays == 1 ? string.Empty : "s")}"
            : $"{item.DaysLeft} day{(item.DaysLeft == 1 ? string.Empty : "s")} left";

    private async Task LoadAsync(CancellationToken cancellationToken)
    {
        var snapshot = await deletionQueueService.GetSnapshotAsync(cancellationToken);
        PendingUsers = snapshot.Pending;
        HeldUsers = snapshot.Held;
    }

    private string GetActingUserId() =>
        User.FindFirstValue(ClaimTypes.NameIdentifier) ?? User.Identity?.Name ?? "unknown";
}

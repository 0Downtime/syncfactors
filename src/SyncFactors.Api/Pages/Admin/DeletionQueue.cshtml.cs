using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using SyncFactors.Contracts;
using SyncFactors.Domain;

namespace SyncFactors.Api.Pages.Admin;

[Authorize(Roles = "Admin,BreakGlassAdmin")]
public sealed class DeletionQueueModel(
    GraveyardDeletionQueueService deletionQueueService,
    IGraveyardRetentionStore retentionStore,
    TimeProvider timeProvider) : PageModel
{
    private const int PageSize = 25;

    [BindProperty(SupportsGet = true)]
    public string? Filter { get; set; }

    [BindProperty(SupportsGet = true)]
    public int PendingPageNumber { get; set; } = 1;

    [BindProperty(SupportsGet = true)]
    public int HeldPageNumber { get; set; } = 1;

    public IReadOnlyList<GraveyardDeletionQueueItem> PendingUsers { get; private set; } = [];

    public IReadOnlyList<GraveyardDeletionQueueItem> HeldUsers { get; private set; } = [];

    public int TotalPendingUsers { get; private set; }

    public int TotalHeldUsers { get; private set; }

    public int TotalPendingPages { get; private set; } = 1;

    public int TotalHeldPages { get; private set; } = 1;

    public bool HasPreviousPendingPage => PendingPageNumber > 1;

    public bool HasNextPendingPage => PendingPageNumber < TotalPendingPages;

    public bool HasPreviousHeldPage => HeldPageNumber > 1;

    public bool HasNextHeldPage => HeldPageNumber < TotalHeldPages;

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
            return RedirectToCurrentPage();
        }

        var result = await retentionStore.SetHoldAsync(
            workerId,
            isOnHold: true,
            actingUserId: GetActingUserId(),
            changedAtUtc: timeProvider.GetUtcNow(),
            cancellationToken);
        if (result.Succeeded)
        {
            SuccessMessage = $"Placed a deletion hold for worker {workerId}.";
            ErrorMessage = null;
        }
        else
        {
            SuccessMessage = null;
            ErrorMessage = result.Outcome switch
            {
                GraveyardHoldChangeOutcome.ActiveDeletionLease => $"Deletion hold for worker {workerId} was not placed because a deletion lease is active.",
                GraveyardHoldChangeOutcome.NotFound => $"Worker {workerId} is not in the deletion queue.",
                _ => $"Deletion hold for worker {workerId} was not placed because the queue state changed."
            };
        }
        return RedirectToCurrentPage();
    }

    public async Task<IActionResult> OnPostRemoveHoldAsync(string workerId, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(workerId))
        {
            ErrorMessage = "Worker ID is required to remove a hold.";
            SuccessMessage = null;
            return RedirectToCurrentPage();
        }

        var result = await retentionStore.SetHoldAsync(
            workerId,
            isOnHold: false,
            actingUserId: GetActingUserId(),
            changedAtUtc: timeProvider.GetUtcNow(),
            cancellationToken);
        if (result.Succeeded)
        {
            SuccessMessage = $"Removed the deletion hold for worker {workerId}.";
            ErrorMessage = null;
        }
        else
        {
            SuccessMessage = null;
            ErrorMessage = result.Outcome switch
            {
                GraveyardHoldChangeOutcome.ActiveDeletionLease => $"Deletion hold for worker {workerId} was not removed because a deletion lease is active.",
                GraveyardHoldChangeOutcome.NotFound => $"Worker {workerId} is not in the deletion queue.",
                _ => $"Deletion hold for worker {workerId} was not removed because the queue state changed."
            };
        }
        return RedirectToCurrentPage();
    }

    public async Task<IActionResult> OnPostApproveDeleteAsync(string workerId, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(workerId))
        {
            ErrorMessage = "Worker ID is required to approve deletion.";
            SuccessMessage = null;
            return RedirectToCurrentPage();
        }

        ErrorMessage = RunQueueProtocol.DeletionCapabilityDisabledMessage;
        SuccessMessage = null;

        return RedirectToCurrentPage();
    }

    public static string FormatCountdown(GraveyardDeletionQueueItem item) =>
        item.OverdueDays > 0
            ? $"Overdue by {item.OverdueDays} day{(item.OverdueDays == 1 ? string.Empty : "s")}"
            : $"{item.DaysLeft} day{(item.DaysLeft == 1 ? string.Empty : "s")} left";

    public static string FormatStatus(GraveyardDeletionQueueItem item)
    {
        var status = EmploymentStatusDisplay.Describe(item.Status);
        if (status is null)
        {
            return item.Status;
        }

        return string.Equals(status.Code, status.Label, StringComparison.OrdinalIgnoreCase)
            ? status.Code
            : $"{status.Label} ({status.Code})";
    }

    private async Task LoadAsync(CancellationToken cancellationToken)
    {
        var snapshot = await deletionQueueService.GetSnapshotAsync(cancellationToken);
        var filteredPending = ApplyFilter(snapshot.Pending);
        var filteredHeld = ApplyFilter(snapshot.Held);

        TotalPendingUsers = filteredPending.Count;
        TotalHeldUsers = filteredHeld.Count;
        TotalPendingPages = Math.Max(1, (int)Math.Ceiling(TotalPendingUsers / (double)PageSize));
        TotalHeldPages = Math.Max(1, (int)Math.Ceiling(TotalHeldUsers / (double)PageSize));

        PendingPageNumber = Math.Clamp(PendingPageNumber, 1, TotalPendingPages);
        HeldPageNumber = Math.Clamp(HeldPageNumber, 1, TotalHeldPages);

        PendingUsers = filteredPending
            .Skip((PendingPageNumber - 1) * PageSize)
            .Take(PageSize)
            .ToArray();
        HeldUsers = filteredHeld
            .Skip((HeldPageNumber - 1) * PageSize)
            .Take(PageSize)
            .ToArray();
    }

    private IReadOnlyList<GraveyardDeletionQueueItem> ApplyFilter(IReadOnlyList<GraveyardDeletionQueueItem> users)
    {
        if (string.IsNullOrWhiteSpace(Filter))
        {
            return users;
        }

        var filter = Filter.Trim();
        return users
            .Where(user =>
                Contains(user.WorkerId, filter) ||
                Contains(user.SamAccountName, filter) ||
                Contains(user.DisplayName, filter) ||
                Contains(user.Status, filter) ||
                Contains(FormatStatus(user), filter) ||
                Contains(user.DistinguishedName, filter) ||
                Contains(user.HoldPlacedBy, filter))
            .ToArray();
    }

    private RedirectToPageResult RedirectToCurrentPage() =>
        RedirectToPage(new
        {
            Filter,
            PendingPageNumber,
            HeldPageNumber
        });

    private static bool Contains(string? value, string filter) =>
        !string.IsNullOrWhiteSpace(value) &&
        value.Contains(filter, StringComparison.OrdinalIgnoreCase);

    private string GetActingUserId() =>
        User.Identity?.Name ?? User.FindFirstValue(ClaimTypes.NameIdentifier) ?? "unknown";
}

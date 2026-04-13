using SyncFactors.Contracts;

namespace SyncFactors.Domain;

public sealed class GraveyardDeletionQueueService(
    IGraveyardRetentionStore retentionStore,
    IDirectoryGateway directoryGateway,
    GraveyardDeletionQueueSettings settings,
    LifecyclePolicySettings lifecycleSettings,
    TimeProvider timeProvider)
{
    public async Task<GraveyardDeletionQueueSnapshot> GetSnapshotAsync(CancellationToken cancellationToken)
    {
        var records = await retentionStore.ListActiveAsync(cancellationToken);
        if (records.Count == 0)
        {
            return new GraveyardDeletionQueueSnapshot([], []);
        }

        var graveyardUsers = await directoryGateway.ListUsersInOuAsync(lifecycleSettings.GraveyardOu, cancellationToken);
        var usersByWorkerId = new Dictionary<string, DirectoryUserSnapshot>(StringComparer.OrdinalIgnoreCase);
        var usersByDistinguishedName = new Dictionary<string, DirectoryUserSnapshot>(StringComparer.OrdinalIgnoreCase);

        foreach (var user in graveyardUsers)
        {
            if (!string.IsNullOrWhiteSpace(user.DistinguishedName))
            {
                usersByDistinguishedName[user.DistinguishedName] = user;
            }

            if (user.Attributes.TryGetValue(lifecycleSettings.DirectoryIdentityAttribute, out var workerId) &&
                !string.IsNullOrWhiteSpace(workerId))
            {
                usersByWorkerId[workerId] = user;
            }
        }

        var now = timeProvider.GetUtcNow();
        var pending = new List<GraveyardDeletionQueueItem>();
        var held = new List<GraveyardDeletionQueueItem>();

        foreach (var record in records)
        {
            if (!TryResolveCurrentDirectoryUser(record, usersByWorkerId, usersByDistinguishedName, out var directoryUser))
            {
                continue;
            }

            var item = CreateQueueItem(record, directoryUser, now);
            if (item.IsOnHold)
            {
                held.Add(item);
            }
            else
            {
                pending.Add(item);
            }
        }

        return new GraveyardDeletionQueueSnapshot(
            Pending: pending
                .OrderBy(item => item.DueDateUtc)
                .ThenBy(item => item.WorkerId, StringComparer.OrdinalIgnoreCase)
                .ToArray(),
            Held: held
                .OrderBy(item => item.DueDateUtc)
                .ThenBy(item => item.WorkerId, StringComparer.OrdinalIgnoreCase)
                .ToArray());
    }

    private GraveyardDeletionQueueItem CreateQueueItem(GraveyardRetentionRecord record, DirectoryUserSnapshot? directoryUser, DateTimeOffset now)
    {
        var anchorDateUtc = record.EndDateUtc ?? record.LastObservedAtUtc;
        var dueDateUtc = anchorDateUtc.Date.AddDays(settings.RetentionDays);
        var dueDate = new DateTimeOffset(dueDateUtc, TimeSpan.Zero);
        var daysUntilDue = (dueDate.Date - now.Date).Days;
        var overdueDays = Math.Max(0, (now.Date - dueDate.Date).Days);

        return new GraveyardDeletionQueueItem(
            WorkerId: record.WorkerId,
            SamAccountName: directoryUser?.SamAccountName ?? record.SamAccountName,
            DisplayName: directoryUser?.DisplayName ?? record.DisplayName,
            DistinguishedName: directoryUser?.DistinguishedName ?? record.DistinguishedName,
            Status: record.Status,
            AnchorDateUtc: anchorDateUtc,
            DueDateUtc: dueDate,
            DaysLeft: Math.Max(0, daysUntilDue),
            OverdueDays: overdueDays,
            IsEligibleForDeletion: daysUntilDue <= 0,
            IsOnHold: record.IsOnHold,
            HoldPlacedAtUtc: record.HoldPlacedAtUtc,
            HoldPlacedBy: record.HoldPlacedBy);
    }

    private static bool TryResolveCurrentDirectoryUser(
        GraveyardRetentionRecord record,
        IReadOnlyDictionary<string, DirectoryUserSnapshot> usersByWorkerId,
        IReadOnlyDictionary<string, DirectoryUserSnapshot> usersByDistinguishedName,
        out DirectoryUserSnapshot? directoryUser)
    {
        if (usersByWorkerId.TryGetValue(record.WorkerId, out directoryUser))
        {
            return true;
        }

        if (!string.IsNullOrWhiteSpace(record.DistinguishedName) &&
            usersByDistinguishedName.TryGetValue(record.DistinguishedName, out directoryUser))
        {
            return true;
        }

        directoryUser = null;
        return false;
    }
}

public sealed record GraveyardDeletionQueueSnapshot(
    IReadOnlyList<GraveyardDeletionQueueItem> Pending,
    IReadOnlyList<GraveyardDeletionQueueItem> Held);

public sealed record GraveyardDeletionQueueItem(
    string WorkerId,
    string? SamAccountName,
    string? DisplayName,
    string? DistinguishedName,
    string Status,
    DateTimeOffset AnchorDateUtc,
    DateTimeOffset DueDateUtc,
    int DaysLeft,
    int OverdueDays,
    bool IsEligibleForDeletion,
    bool IsOnHold,
    DateTimeOffset? HoldPlacedAtUtc,
    string? HoldPlacedBy);

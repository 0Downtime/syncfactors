using SyncFactors.Contracts;
using SyncFactors.Domain;

namespace SyncFactors.Domain.Tests;

public sealed class GraveyardDeletionQueueServiceTests
{
    [Fact]
    public async Task GetSnapshotAsync_SplitsHeldUsersAndSortsPendingUrgentFirst()
    {
        var now = new DateTimeOffset(2026, 4, 11, 12, 0, 0, TimeSpan.Zero);
        var service = new GraveyardDeletionQueueService(
            new StubGraveyardRetentionStore(
                [
                    new GraveyardRetentionRecord(
                        WorkerId: "10003",
                        SamAccountName: "10003",
                        DisplayName: "Held User",
                        DistinguishedName: "CN=Held User,OU=Graveyard,DC=example,DC=com",
                        Status: "T",
                        EndDateUtc: new DateTimeOffset(2026, 2, 1, 0, 0, 0, TimeSpan.Zero),
                        LastObservedAtUtc: new DateTimeOffset(2026, 4, 1, 0, 0, 0, TimeSpan.Zero),
                        Active: true,
                        IsOnHold: true,
                        HoldPlacedAtUtc: new DateTimeOffset(2026, 4, 5, 0, 0, 0, TimeSpan.Zero),
                        HoldPlacedBy: "admin-1"),
                    new GraveyardRetentionRecord(
                        WorkerId: "10002",
                        SamAccountName: "10002",
                        DisplayName: "Future User",
                        DistinguishedName: "CN=Future User,OU=Graveyard,DC=example,DC=com",
                        Status: "T",
                        EndDateUtc: new DateTimeOffset(2026, 3, 20, 0, 0, 0, TimeSpan.Zero),
                        LastObservedAtUtc: new DateTimeOffset(2026, 4, 1, 0, 0, 0, TimeSpan.Zero),
                        Active: true),
                    new GraveyardRetentionRecord(
                        WorkerId: "10001",
                        SamAccountName: "10001",
                        DisplayName: "Overdue User",
                        DistinguishedName: "CN=Overdue User,OU=Graveyard,DC=example,DC=com",
                        Status: "T",
                        EndDateUtc: new DateTimeOffset(2026, 2, 15, 0, 0, 0, TimeSpan.Zero),
                        LastObservedAtUtc: new DateTimeOffset(2026, 4, 1, 0, 0, 0, TimeSpan.Zero),
                        Active: true),
                    new GraveyardRetentionRecord(
                        WorkerId: "10004",
                        SamAccountName: "10004",
                        DisplayName: "Gone User",
                        DistinguishedName: "CN=Gone User,OU=Graveyard,DC=example,DC=com",
                        Status: "T",
                        EndDateUtc: new DateTimeOffset(2026, 2, 15, 0, 0, 0, TimeSpan.Zero),
                        LastObservedAtUtc: new DateTimeOffset(2026, 4, 1, 0, 0, 0, TimeSpan.Zero),
                        Active: true)
                ]),
            new StubDirectoryGateway(
                [
                    CreateDirectoryUser("10001", "Overdue User"),
                    CreateDirectoryUser("10002", "Future User"),
                    CreateDirectoryUser("10003", "Held User")
                ]),
            new GraveyardDeletionQueueSettings(RetentionDays: 30, AutoDeleteEnabled: true),
            CreateLifecycleSettings(),
            new FakeTimeProvider(now));

        var snapshot = await service.GetSnapshotAsync(CancellationToken.None);

        Assert.Equal(["10001", "10002"], snapshot.Pending.Select(item => item.WorkerId).ToArray());
        Assert.Single(snapshot.Held);
        Assert.Equal("10003", snapshot.Held[0].WorkerId);
        Assert.True(snapshot.Pending[0].OverdueDays > 0);
        Assert.False(snapshot.Pending[1].IsEligibleForDeletion);
    }

    private static LifecyclePolicySettings CreateLifecycleSettings() =>
        new(
            ActiveOu: "OU=Employees,DC=example,DC=com",
            PrehireOu: "OU=Prehire,DC=example,DC=com",
            GraveyardOu: "OU=Graveyard,DC=example,DC=com",
            InactiveStatusField: "emplStatus",
            InactiveStatusValues: ["T"],
            DirectoryIdentityAttribute: "employeeID");

    private static DirectoryUserSnapshot CreateDirectoryUser(string workerId, string displayName) =>
        new(
            SamAccountName: workerId,
            DistinguishedName: $"CN={displayName},OU=Graveyard,DC=example,DC=com",
            Enabled: false,
            DisplayName: displayName,
            Attributes: new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
            {
                ["employeeID"] = workerId
            });

    private sealed class StubGraveyardRetentionStore(IReadOnlyList<GraveyardRetentionRecord> records) : IGraveyardRetentionStore
    {
        public Task UpsertObservedAsync(GraveyardRetentionRecord record, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task ResolveAsync(string workerId, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task<IReadOnlyList<GraveyardRetentionRecord>> ListActiveAsync(CancellationToken cancellationToken) =>
            Task.FromResult(records);

        public Task SetHoldAsync(string workerId, bool isOnHold, string? actingUserId, DateTimeOffset changedAtUtc, CancellationToken cancellationToken) =>
            Task.CompletedTask;

        public Task<GraveyardRetentionReportStatus> GetReportStatusAsync(CancellationToken cancellationToken) =>
            Task.FromResult(new GraveyardRetentionReportStatus(null, null, null));

        public Task RecordReportAttemptAsync(DateTimeOffset attemptedAt, string? error, DateTimeOffset? sentAtUtc, CancellationToken cancellationToken) =>
            Task.CompletedTask;
    }

    private sealed class StubDirectoryGateway(IReadOnlyList<DirectoryUserSnapshot> users) : IDirectoryGateway
    {
        public Task<DirectoryUserSnapshot?> FindByWorkerAsync(WorkerSnapshot worker, CancellationToken cancellationToken) =>
            Task.FromResult<DirectoryUserSnapshot?>(null);

        public Task<IReadOnlyList<DirectoryUserSnapshot>> ListUsersInOuAsync(string ouDistinguishedName, CancellationToken cancellationToken) =>
            Task.FromResult(users);

        public Task<string?> ResolveManagerDistinguishedNameAsync(string managerId, CancellationToken cancellationToken) =>
            Task.FromResult<string?>(null);

        public Task<string> ResolveAvailableEmailLocalPartAsync(WorkerSnapshot worker, bool isCreate, CancellationToken cancellationToken) =>
            Task.FromResult(worker.WorkerId.ToLowerInvariant());
    }

    private sealed class FakeTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}

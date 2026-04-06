using Microsoft.Extensions.Logging.Abstractions;
using SyncFactors.Contracts;
using SyncFactors.Domain;

namespace SyncFactors.Domain.Tests;

public sealed class GraveyardRetentionReportCoordinatorTests
{
    [Fact]
    public async Task TrySendDueReportAsync_SendsWeeklyEmailForOverdueGraveyardUsers()
    {
        var now = new DateTimeOffset(2026, 4, 6, 12, 0, 0, TimeSpan.Zero);
        var retentionStore = new StubGraveyardRetentionStore(
            [
                new GraveyardRetentionRecord(
                    WorkerId: "10001",
                    SamAccountName: "10001",
                    DisplayName: "Retired User",
                    DistinguishedName: "CN=Retired User,OU=Graveyard,DC=example,DC=com",
                    Status: "64308",
                    EndDateUtc: new DateTimeOffset(2026, 2, 1, 0, 0, 0, TimeSpan.Zero),
                    LastObservedAtUtc: new DateTimeOffset(2026, 2, 15, 0, 0, 0, TimeSpan.Zero),
                    Active: true)
            ]);
        var emailSender = new CapturingEmailSender();
        var coordinator = new GraveyardRetentionReportCoordinator(
            retentionStore,
            new StubDirectoryGateway(
                [
                    new DirectoryUserSnapshot(
                        SamAccountName: "10001",
                        DistinguishedName: "CN=Retired User,OU=Graveyard,DC=example,DC=com",
                        Enabled: false,
                        DisplayName: "Retired User",
                        Attributes: new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
                        {
                            ["employeeID"] = "10001"
                        })
                ]),
            emailSender,
            new GraveyardRetentionNotificationSettings(
                Enabled: true,
                IntervalDays: 7,
                RetentionDays: 45,
                SubjectPrefix: "[SyncFactors]",
                Recipients: ["ops@example.com"]),
            new LifecyclePolicySettings(
                ActiveOu: "OU=Employees,DC=example,DC=com",
                PrehireOu: "OU=Prehire,DC=example,DC=com",
                GraveyardOu: "OU=Graveyard,DC=example,DC=com",
                InactiveStatusField: "emplStatus",
                InactiveStatusValues: ["64307", "64308"],
                DirectoryIdentityAttribute: "employeeID"),
            new FakeTimeProvider(now),
            NullLogger<GraveyardRetentionReportCoordinator>.Instance);

        var sent = await coordinator.TrySendDueReportAsync(CancellationToken.None);

        Assert.True(sent);
        Assert.Equal("[SyncFactors] Graveyard Users Past Retention (1)", emailSender.Subject);
        Assert.Contains("10001", emailSender.Body);
        Assert.Contains("DaysPastRetention: 19", emailSender.Body);
        Assert.Equal(now, retentionStore.LastSentAtUtc);
    }

    private sealed class StubGraveyardRetentionStore(IReadOnlyList<GraveyardRetentionRecord> records) : IGraveyardRetentionStore
    {
        public DateTimeOffset? LastSentAtUtc { get; private set; }

        public Task UpsertObservedAsync(GraveyardRetentionRecord record, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task ResolveAsync(string workerId, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task<IReadOnlyList<GraveyardRetentionRecord>> ListActiveAsync(CancellationToken cancellationToken) =>
            Task.FromResult(records);

        public Task<GraveyardRetentionReportStatus> GetReportStatusAsync(CancellationToken cancellationToken) =>
            Task.FromResult(new GraveyardRetentionReportStatus(null, null, null));

        public Task RecordReportAttemptAsync(DateTimeOffset attemptedAt, string? error, DateTimeOffset? sentAtUtc, CancellationToken cancellationToken)
        {
            _ = attemptedAt;
            _ = error;
            LastSentAtUtc = sentAtUtc;
            return Task.CompletedTask;
        }
    }

    private sealed class StubDirectoryGateway(IReadOnlyList<DirectoryUserSnapshot> graveyardUsers) : IDirectoryGateway
    {
        public Task<DirectoryUserSnapshot?> FindByWorkerAsync(WorkerSnapshot worker, CancellationToken cancellationToken) =>
            Task.FromResult<DirectoryUserSnapshot?>(null);

        public Task<IReadOnlyList<DirectoryUserSnapshot>> ListUsersInOuAsync(string ouDistinguishedName, CancellationToken cancellationToken) =>
            Task.FromResult(graveyardUsers);

        public Task<string?> ResolveManagerDistinguishedNameAsync(string managerId, CancellationToken cancellationToken) =>
            Task.FromResult<string?>(null);

        public Task<string> ResolveAvailableEmailLocalPartAsync(WorkerSnapshot worker, bool isCreate, CancellationToken cancellationToken) =>
            Task.FromResult("test.user");
    }

    private sealed class CapturingEmailSender : IEmailSender
    {
        public string? Subject { get; private set; }
        public string? Body { get; private set; }

        public Task SendAsync(string subject, string body, IReadOnlyList<string> recipients, CancellationToken cancellationToken)
        {
            _ = recipients;
            _ = cancellationToken;
            Subject = subject;
            Body = body;
            return Task.CompletedTask;
        }
    }

    private sealed class FakeTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}

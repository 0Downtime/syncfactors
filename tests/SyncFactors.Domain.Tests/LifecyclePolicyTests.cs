using SyncFactors.Contracts;
using SyncFactors.Domain;

namespace SyncFactors.Domain.Tests;

public sealed class LifecyclePolicyTests
{
    [Fact]
    public void Evaluate_LeaveWorker_RoutesToLeaveOuAndDisablesAccount()
    {
        var policy = CreatePolicy();
        var worker = CreateWorker("20001", "64304");
        var directoryUser = new DirectoryUserSnapshot(
            SamAccountName: "20001",
            DistinguishedName: "CN=Worker 20001,OU=Employees,DC=example,DC=com",
            Enabled: true,
            DisplayName: "Worker 20001",
            Attributes: new Dictionary<string, string?>());

        var decision = policy.Evaluate(worker, directoryUser);

        Assert.Equal("updates", decision.Bucket);
        Assert.Equal("OU=Leave Users,DC=example,DC=com", decision.TargetOu);
        Assert.False(decision.TargetEnabled);
    }

    [Fact]
    public void Evaluate_ActiveWorkerInLeaveOu_ReturnsToActiveOuAndEnablesAccount()
    {
        var policy = CreatePolicy();
        var worker = CreateWorker("20002", "64300");
        var directoryUser = new DirectoryUserSnapshot(
            SamAccountName: "20002",
            DistinguishedName: "CN=Worker 20002,OU=Leave Users,DC=example,DC=com",
            Enabled: false,
            DisplayName: "Worker 20002",
            Attributes: new Dictionary<string, string?>());

        var decision = policy.Evaluate(worker, directoryUser);

        Assert.Equal("enables", decision.Bucket);
        Assert.Equal("OU=Employees,DC=example,DC=com", decision.TargetOu);
        Assert.True(decision.TargetEnabled);
    }

    [Fact]
    public void Evaluate_TerminatedWorker_RoutesToGraveyardAndDisablesAccount()
    {
        var policy = CreatePolicy();
        var worker = CreateWorker("20003", "64308");
        var directoryUser = new DirectoryUserSnapshot(
            SamAccountName: "20003",
            DistinguishedName: "CN=Worker 20003,OU=Employees,DC=example,DC=com",
            Enabled: true,
            DisplayName: "Worker 20003",
            Attributes: new Dictionary<string, string?>());

        var decision = policy.Evaluate(worker, directoryUser);

        Assert.Equal("graveyardMoves", decision.Bucket);
        Assert.Equal("OU=Graveyard,DC=example,DC=com", decision.TargetOu);
        Assert.False(decision.TargetEnabled);
    }

    [Fact]
    public void Evaluate_DisabledCreatePastDeletionRetention_WhenToggleEnabled_SkipsCreate()
    {
        var policy = CreatePolicy(skipCreateIfPastDeletionRetention: true, deletionRetentionDays: 45);
        var worker = CreateWorker(
            workerId: "20004",
            status: "64304",
            inactiveDate: DateTimeOffset.UtcNow.Date.AddDays(-46).ToString("yyyy-MM-dd"));
        var directoryUser = new DirectoryUserSnapshot(
            SamAccountName: null,
            DistinguishedName: null,
            Enabled: null,
            DisplayName: null,
            Attributes: new Dictionary<string, string?>());

        var decision = policy.Evaluate(worker, directoryUser);

        Assert.Equal("unchanged", decision.Bucket);
        Assert.Equal("OU=Leave Users,DC=example,DC=com", decision.TargetOu);
        Assert.False(decision.TargetEnabled);
    }

    [Fact]
    public void Evaluate_DisabledCreatePastDeletionRetention_WhenToggleDisabled_StillCreates()
    {
        var policy = CreatePolicy(skipCreateIfPastDeletionRetention: false, deletionRetentionDays: 45);
        var worker = CreateWorker(
            workerId: "20005",
            status: "64304",
            inactiveDate: DateTimeOffset.UtcNow.Date.AddDays(-46).ToString("yyyy-MM-dd"));
        var directoryUser = new DirectoryUserSnapshot(
            SamAccountName: null,
            DistinguishedName: null,
            Enabled: null,
            DisplayName: null,
            Attributes: new Dictionary<string, string?>());

        var decision = policy.Evaluate(worker, directoryUser);

        Assert.Equal("creates", decision.Bucket);
        Assert.Equal("OU=Leave Users,DC=example,DC=com", decision.TargetOu);
        Assert.False(decision.TargetEnabled);
    }

    private static LifecyclePolicy CreatePolicy(
        bool skipCreateIfPastDeletionRetention = false,
        int deletionRetentionDays = 0)
    {
        return new LifecyclePolicy(
            new LifecyclePolicySettings(
                ActiveOu: "OU=Employees,DC=example,DC=com",
                PrehireOu: "OU=Prehire,DC=example,DC=com",
                GraveyardOu: "OU=Graveyard,DC=example,DC=com",
                InactiveStatusField: "emplStatus",
                InactiveStatusValues: ["64307", "64308"],
                LeaveOu: "OU=Leave Users,DC=example,DC=com",
                LeaveStatusValues: ["64303", "64304"],
                InactiveDateField: "endDate",
                DeletionRetentionDays: deletionRetentionDays,
                SkipCreateIfPastDeletionRetention: skipCreateIfPastDeletionRetention));
    }

    private static WorkerSnapshot CreateWorker(string workerId, string status, string? inactiveDate = null)
    {
        var attributes = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
        {
            ["emplStatus"] = status
        };
        if (!string.IsNullOrWhiteSpace(inactiveDate))
        {
            attributes["endDate"] = inactiveDate;
        }

        return new WorkerSnapshot(
            WorkerId: workerId,
            PreferredName: $"Worker{workerId}",
            LastName: "Sample",
            Department: "Operations",
            TargetOu: "OU=Employees,DC=example,DC=com",
            IsPrehire: false,
            Attributes: attributes);
    }
}

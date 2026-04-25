using Microsoft.Extensions.Logging.Abstractions;
using SyncFactors.Contracts;
using SyncFactors.Domain;

namespace SyncFactors.Domain.Tests;

public sealed class WorkerPlanningServiceIdentityCorrelationTests
{
    [Fact]
    public async Task PlanAsync_ActiveSuccessorMatch_UpdatesExistingUserAndWritesCorrelationAttributes()
    {
        var directoryUser = CreateDirectoryUser(
            employeeId: "10000",
            successorPersonIdExternal: "20000",
            previousPersonIdExternal: null);
        var service = CreateService(directoryUser);

        var plan = await service.PlanAsync(CreateWorker("20000", "64300"), logPath: null, CancellationToken.None);

        Assert.Equal("updates", plan.Bucket);
        Assert.DoesNotContain(plan.Operations, operation => operation.Kind == "CreateUser");
        Assert.Contains(plan.Operations, operation => operation.Kind == "UpdateUser");
        Assert.Contains(plan.AttributeChanges, change =>
            change.Attribute == "extensionAttribute15" &&
            change.Before == "(unset)" &&
            change.After == "10000" &&
            change.Changed);
        Assert.Contains(plan.AttributeChanges, change =>
            change.Attribute == "extensionAttribute14" &&
            change.Before == "20000" &&
            change.After == "(unset)" &&
            change.Changed);
    }

    [Fact]
    public async Task PlanAsync_InactiveCurrentIdentityWithSuccessor_SuppressesDisable()
    {
        var directoryUser = CreateDirectoryUser(
            employeeId: "10000",
            successorPersonIdExternal: "20000",
            previousPersonIdExternal: null);
        var service = CreateService(directoryUser);

        var plan = await service.PlanAsync(CreateWorker("10000", "T"), logPath: null, CancellationToken.None);

        Assert.Equal("unchanged", plan.Bucket);
        Assert.Empty(plan.Operations);
        Assert.Equal("Worker superseded by linked successor.", plan.Reason);
    }

    [Fact]
    public async Task PlanAsync_InactivePreviousIdentityMatch_SuppressesDisable()
    {
        var directoryUser = CreateDirectoryUser(
            employeeId: "20000",
            successorPersonIdExternal: null,
            previousPersonIdExternal: "10000");
        var service = CreateService(directoryUser);

        var plan = await service.PlanAsync(CreateWorker("10000", "T"), logPath: null, CancellationToken.None);

        Assert.Equal("unchanged", plan.Bucket);
        Assert.Empty(plan.Operations);
        Assert.Equal("Worker superseded by linked successor.", plan.Reason);
    }

    [Fact]
    public async Task PlanAsync_ConflictingPreviousIdentityValue_RequiresManualReview()
    {
        var directoryUser = CreateDirectoryUser(
            employeeId: "10000",
            successorPersonIdExternal: "20000",
            previousPersonIdExternal: "99999");
        var service = CreateService(directoryUser);

        var plan = await service.PlanAsync(CreateWorker("20000", "64300"), logPath: null, CancellationToken.None);

        Assert.Equal("manualReview", plan.Bucket);
        Assert.Equal("DirectoryIdentity", plan.ReviewCategory);
        Assert.Equal("ConflictingIdentityCorrelation", plan.ReviewCaseType);
        Assert.Empty(plan.Operations);
    }

    private static WorkerPlanningService CreateService(DirectoryUserSnapshot directoryUser)
    {
        return new WorkerPlanningService(
            new StubDirectoryGateway(directoryUser),
            new IdentityMatcher(),
            new LifecyclePolicy(new LifecyclePolicySettings(
                ActiveOu: "OU=Employees,DC=example,DC=com",
                PrehireOu: "OU=Prehire,DC=example,DC=com",
                GraveyardOu: "OU=Graveyard,DC=example,DC=com",
                InactiveStatusField: "emplStatus",
                InactiveStatusValues: ["T"],
                DirectoryIdentityAttribute: "employeeID")),
            new EmptyAttributeDiffService(),
            new EmptyAttributeMappingProvider(),
            NullLogger<WorkerPlanningService>.Instance,
            identityCorrelationSettings: new IdentityCorrelationSettings(
                Enabled: true,
                IdentityAttribute: "employeeID",
                SuccessorPersonIdExternalAttribute: "extensionAttribute14",
                PreviousPersonIdExternalAttribute: "extensionAttribute15"));
    }

    private static WorkerSnapshot CreateWorker(string workerId, string emplStatus)
    {
        return new WorkerSnapshot(
            WorkerId: workerId,
            PreferredName: "Chris",
            LastName: "Brien",
            Department: "IT",
            TargetOu: "OU=Employees,DC=example,DC=com",
            IsPrehire: false,
            Attributes: new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
            {
                ["emplStatus"] = emplStatus
            });
    }

    private static DirectoryUserSnapshot CreateDirectoryUser(
        string employeeId,
        string? successorPersonIdExternal,
        string? previousPersonIdExternal)
    {
        return new DirectoryUserSnapshot(
            SamAccountName: "cbrien",
            DistinguishedName: "CN=cbrien,OU=Employees,DC=example,DC=com",
            Enabled: true,
            DisplayName: "Brien, Chris",
            Attributes: new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
            {
                ["employeeID"] = employeeId,
                ["sAMAccountName"] = "cbrien",
                ["cn"] = "cbrien",
                ["displayName"] = "Brien, Chris",
                ["UserPrincipalName"] = "cbrien@example.test",
                ["mail"] = "cbrien@example.test",
                ["extensionAttribute14"] = successorPersonIdExternal,
                ["extensionAttribute15"] = previousPersonIdExternal
            });
    }

    private sealed class StubDirectoryGateway(DirectoryUserSnapshot directoryUser) : IDirectoryGateway
    {
        public Task<DirectoryUserSnapshot?> FindByWorkerAsync(WorkerSnapshot worker, CancellationToken cancellationToken)
        {
            _ = worker;
            _ = cancellationToken;
            return Task.FromResult<DirectoryUserSnapshot?>(directoryUser);
        }

        public Task<string?> ResolveManagerDistinguishedNameAsync(string managerId, CancellationToken cancellationToken)
        {
            _ = managerId;
            _ = cancellationToken;
            return Task.FromResult<string?>(null);
        }

        public Task<string> ResolveAvailableEmailLocalPartAsync(WorkerSnapshot worker, bool isCreate, CancellationToken cancellationToken)
        {
            _ = worker;
            _ = isCreate;
            _ = cancellationToken;
            return Task.FromResult("cbrien");
        }

        public Task<string> ResolveAvailableEmailLocalPartAsync(
            WorkerSnapshot worker,
            bool isCreate,
            DirectoryUserSnapshot? existingDirectoryUser,
            CancellationToken cancellationToken)
        {
            _ = worker;
            _ = isCreate;
            _ = existingDirectoryUser;
            _ = cancellationToken;
            return Task.FromResult("cbrien");
        }
    }

    private sealed class EmptyAttributeDiffService : IAttributeDiffService
    {
        public Task<IReadOnlyList<AttributeChange>> BuildDiffAsync(
            WorkerSnapshot worker,
            DirectoryUserSnapshot? directoryUser,
            string? proposedEmailAddress,
            string? logPath,
            CancellationToken cancellationToken)
        {
            _ = worker;
            _ = directoryUser;
            _ = proposedEmailAddress;
            _ = logPath;
            _ = cancellationToken;
            return Task.FromResult<IReadOnlyList<AttributeChange>>([]);
        }
    }

    private sealed class EmptyAttributeMappingProvider : IAttributeMappingProvider
    {
        public IReadOnlyList<AttributeMapping> GetEnabledMappings() => [];
    }
}

using Microsoft.Extensions.Logging.Abstractions;
using SyncFactors.Contracts;
using SyncFactors.Domain;

namespace SyncFactors.Domain.Tests;

public sealed class WorkerPlanningServiceTests
{
    [Fact]
    public async Task PlanAsync_SourceReviewBlocksAutomaticPlanningAndSkipsDiffs()
    {
        var service = CreateService(
            directoryUser: null,
            attributeDiffService: new ThrowingAttributeDiffService(),
            attributeMappingProvider: new StaticAttributeMappingProvider(
            [
                new AttributeMapping("missingRequired", "department", Required: true, Transform: "copy")
            ]));

        var plan = await service.PlanAsync(CreateWorker("10001", new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
        {
            ["emplStatus"] = "64300",
            ["_syncfactors.reviewCategory"] = "SourceData",
            ["_syncfactors.reviewCaseType"] = "AmbiguousPersonalInfo",
            ["_syncfactors.reviewReason"] = "Multiple personalInfo rows."
        }), logPath: null, CancellationToken.None);

        Assert.Equal("manualReview", plan.Bucket);
        Assert.Equal("SourceData", plan.ReviewCategory);
        Assert.Equal("AmbiguousPersonalInfo", plan.ReviewCaseType);
        Assert.Equal("Multiple personalInfo rows.", plan.Reason);
        Assert.False(plan.CanAutoApply);
        Assert.Empty(plan.Operations);
        Assert.NotNull(plan.DecisionSteps);
        var decisionSteps = plan.DecisionSteps;
        Assert.Contains(decisionSteps, step => step.Step == "Source Data" && step.Outcome == "Blocked");
        Assert.Contains(decisionSteps, step => step.Step == "Required Inputs" && step.Outcome == "Skipped");
    }

    [Fact]
    public async Task PlanAsync_MatchedDifferentSamAccountNameWithSamIdentityRequiresManualReview()
    {
        var service = CreateService(
            directoryUser: new DirectoryUserSnapshot(
                SamAccountName: "46305",
                DistinguishedName: "CN=46305,OU=Employees,DC=example,DC=com",
                Enabled: true,
                DisplayName: "Worker 46305",
                Attributes: new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
                {
                    ["sAMAccountName"] = "46305",
                    ["UserPrincipalName"] = "46305@example.com",
                    ["mail"] = "46305@example.com"
                }),
            attributeDiffService: new ThrowingAttributeDiffService(),
            identityCorrelationSettings: new IdentityCorrelationSettings(
                Enabled: false,
                IdentityAttribute: "sAMAccountName",
                SuccessorPersonIdExternalAttribute: null,
                PreviousPersonIdExternalAttribute: null));

        var plan = await service.PlanAsync(CreateWorker("46309", new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
        {
            ["emplStatus"] = "64300",
            ["personIdExternal"] = "46305",
            ["userId"] = "46309"
        }), logPath: null, CancellationToken.None);

        Assert.Equal("manualReview", plan.Bucket);
        Assert.Equal("DirectoryIdentity", plan.ReviewCategory);
        Assert.Equal("SourceIdentityMismatch", plan.ReviewCaseType);
        Assert.Contains("Matched AD account '46305'", plan.Reason);
        Assert.False(plan.CanAutoApply);
        Assert.Empty(plan.Operations);
        Assert.NotNull(plan.DecisionSteps);
        var decisionSteps = plan.DecisionSteps;
        Assert.Contains(decisionSteps, step => step.Step == "Directory Identity" && step.Outcome == "Blocked");
    }

    [Fact]
    public async Task PlanAsync_ResolvedManagerAddsManagerAttributeChangeAndUpdateOperation()
    {
        var directoryGateway = new StubDirectoryGateway(
            CreateDirectoryUser("10001", manager: "CN=Old Manager,OU=Employees,DC=example,DC=com"),
            managerDistinguishedName: "CN=New Manager,OU=Employees,DC=example,DC=com");
        var service = CreateService(
            directoryUser: directoryGateway.DirectoryUser,
            directoryGateway: directoryGateway,
            attributeDiffService: new StaticAttributeDiffService([]),
            attributeMappingProvider: new StaticAttributeMappingProvider([]));

        var plan = await service.PlanAsync(CreateWorker("10001", new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
        {
            ["emplStatus"] = "64300",
            ["managerId"] = "20001"
        }), logPath: null, CancellationToken.None);

        var managerChange = Assert.Single(plan.AttributeChanges, change => change.Attribute == "manager");
        Assert.Equal("CN=Old Manager,OU=Employees,DC=example,DC=com", managerChange.Before);
        Assert.Equal("CN=New Manager,OU=Employees,DC=example,DC=com", managerChange.After);
        Assert.True(managerChange.Changed);
        Assert.Equal("updates", plan.Bucket);
        Assert.Contains(plan.Operations, operation => operation.Kind == "UpdateUser");
        Assert.Equal("CN=New Manager,OU=Employees,DC=example,DC=com", plan.ManagerDistinguishedName);
        Assert.NotNull(plan.DecisionSteps);
        var decisionSteps = plan.DecisionSteps;
        Assert.Contains(decisionSteps, step => step.Step == "Manager Resolution" && step.Outcome == "Resolved");
    }

    [Fact]
    public async Task PlanAsync_MissingRequiredSourceMappingBlocksAutoSync()
    {
        var service = CreateService(
            directoryUser: CreateDirectoryUser("10001"),
            attributeDiffService: new StaticAttributeDiffService(
            [
                new AttributeChange("department", "department", "Old", "New", Changed: true)
            ]),
            attributeMappingProvider: new StaticAttributeMappingProvider(
            [
                new AttributeMapping("missingRequired", "department", Required: true, Transform: "copy")
            ]));

        var plan = await service.PlanAsync(CreateWorker("10001", new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
        {
            ["emplStatus"] = "64300",
            ["department"] = "New"
        }), logPath: null, CancellationToken.None);

        var missing = Assert.Single(plan.MissingSourceAttributes);
        Assert.Equal("missingRequired", missing.Attribute);
        Assert.Equal("Required mapping for department has no value.", missing.Reason);
        Assert.Equal("unchanged", plan.Bucket);
        Assert.False(plan.CanAutoApply);
        Assert.Empty(plan.Operations);
        Assert.Equal("Required mapping for department has no value.", plan.Reason);
        Assert.NotNull(plan.DecisionSteps);
        var decisionSteps = plan.DecisionSteps;
        Assert.Contains(decisionSteps, step => step.Step == "Required Inputs" && step.Outcome == "Blocked");
    }

    private static WorkerPlanningService CreateService(
        DirectoryUserSnapshot? directoryUser,
        IDirectoryGateway? directoryGateway = null,
        IAttributeDiffService? attributeDiffService = null,
        IAttributeMappingProvider? attributeMappingProvider = null,
        IdentityCorrelationSettings? identityCorrelationSettings = null)
    {
        return new WorkerPlanningService(
            directoryGateway ?? new StubDirectoryGateway(directoryUser, managerDistinguishedName: null),
            new IdentityMatcher(),
            new LifecyclePolicy(new LifecyclePolicySettings(
                ActiveOu: "OU=Employees,DC=example,DC=com",
                PrehireOu: "OU=Prehire,DC=example,DC=com",
                GraveyardOu: "OU=Graveyard,DC=example,DC=com",
                InactiveStatusField: "emplStatus",
                InactiveStatusValues: ["64308", "T"],
                DirectoryIdentityAttribute: "employeeID")),
            attributeDiffService ?? new StaticAttributeDiffService([]),
            attributeMappingProvider ?? new StaticAttributeMappingProvider([]),
            NullLogger<WorkerPlanningService>.Instance,
            identityCorrelationSettings: identityCorrelationSettings);
    }

    private static WorkerSnapshot CreateWorker(string workerId, IReadOnlyDictionary<string, string?> attributes)
    {
        return new WorkerSnapshot(
            WorkerId: workerId,
            PreferredName: "Chris",
            LastName: "Brien",
            Department: attributes.TryGetValue("department", out var department) && !string.IsNullOrWhiteSpace(department)
                ? department
                : "IT",
            TargetOu: "OU=Employees,DC=example,DC=com",
            IsPrehire: false,
            Attributes: attributes);
    }

    private static DirectoryUserSnapshot CreateDirectoryUser(string employeeId, string? manager = null)
    {
        return new DirectoryUserSnapshot(
            SamAccountName: $"user{employeeId}",
            DistinguishedName: $"CN=user{employeeId},OU=Employees,DC=example,DC=com",
            Enabled: true,
            DisplayName: $"Worker {employeeId}",
            Attributes: new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
            {
                ["employeeID"] = employeeId,
                ["sAMAccountName"] = $"user{employeeId}",
                ["UserPrincipalName"] = $"user{employeeId}@example.com",
                ["mail"] = $"user{employeeId}@example.com",
                ["manager"] = manager
            });
    }

    private sealed class StubDirectoryGateway(
        DirectoryUserSnapshot? directoryUser,
        string? managerDistinguishedName) : IDirectoryGateway
    {
        public DirectoryUserSnapshot? DirectoryUser => directoryUser;

        public Task<DirectoryUserSnapshot?> FindByWorkerAsync(WorkerSnapshot worker, CancellationToken cancellationToken)
        {
            _ = worker;
            _ = cancellationToken;
            return Task.FromResult(directoryUser);
        }

        public Task<string?> ResolveManagerDistinguishedNameAsync(string managerId, CancellationToken cancellationToken)
        {
            _ = managerId;
            _ = cancellationToken;
            return Task.FromResult(managerDistinguishedName);
        }

        public Task<string> ResolveAvailableEmailLocalPartAsync(WorkerSnapshot worker, bool isCreate, CancellationToken cancellationToken)
        {
            _ = isCreate;
            _ = cancellationToken;
            return Task.FromResult($"user{worker.WorkerId}");
        }

        public Task<string> ResolveAvailableEmailLocalPartAsync(
            WorkerSnapshot worker,
            bool isCreate,
            DirectoryUserSnapshot? existingDirectoryUser,
            CancellationToken cancellationToken)
        {
            _ = isCreate;
            _ = existingDirectoryUser;
            _ = cancellationToken;
            return Task.FromResult($"user{worker.WorkerId}");
        }
        public Task<IReadOnlyList<DirectoryUserSnapshot>> ListUsersInOuAsync(string ouDistinguishedName, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<DirectoryUserSnapshot>>([]);

    }

    private sealed class StaticAttributeDiffService(IReadOnlyList<AttributeChange> changes) : IAttributeDiffService
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
            return Task.FromResult(changes);
        }
    }

    private sealed class ThrowingAttributeDiffService : IAttributeDiffService
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
            throw new InvalidOperationException("Diff generation should have been skipped.");
        }
    }

    private sealed class StaticAttributeMappingProvider(IReadOnlyList<AttributeMapping> mappings) : IAttributeMappingProvider
    {
        public IReadOnlyList<AttributeMapping> GetEnabledMappings() => mappings;
    }
}

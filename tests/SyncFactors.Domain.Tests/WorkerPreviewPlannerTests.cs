using Microsoft.Extensions.Logging.Abstractions;
using SyncFactors.Contracts;
using SyncFactors.Domain;

namespace SyncFactors.Domain.Tests;

public sealed class WorkerPreviewPlannerTests
{
    [Fact]
    public async Task PreviewAsync_PersistsExistingUsersWithoutDiffsAsUnchanged()
    {
        var worker = new WorkerSnapshot(
            WorkerId: "44522",
            PreferredName: "Christopher",
            LastName: "Brien",
            Department: "Infrastructure & Security",
            TargetOu: "OU=Employees,DC=example,DC=com",
            IsPrehire: false,
            Attributes: new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
            {
                ["company"] = "Spire Services, Inc.",
                ["department"] = "Infrastructure & Security"
            });

        var runRepository = new CapturingRunRepository();
        var planner = new WorkerPreviewPlanner(
            new StubWorkerSource(worker),
            new WorkerPlanningService(
                new ExistingUserDirectoryGateway(),
                new ExistingUserIdentityMatcher(),
                CreateLifecyclePolicy(),
                new UnchangedAttributeDiffService(),
                new StubAttributeMappingProvider(),
                NullLogger<WorkerPlanningService>.Instance),
            new StubAttributeMappingProvider(),
            new StubWorkerPreviewLogWriter(),
            runRepository,
            NullLogger<WorkerPreviewPlanner>.Instance);

        var preview = await planner.PreviewAsync("44522", CancellationToken.None);

        Assert.Equal("unchanged", preview.Buckets.Single());
        Assert.Equal(1, runRepository.SavedRuns.Single().Unchanged);
        Assert.Equal(0, runRepository.SavedRuns.Single().Updates);
        Assert.Equal("unchanged", runRepository.ReplacedEntries.Single().entries.Single().Bucket);
    }

    [Fact]
    public async Task PreviewAsync_IncludesPopulatedSourceAttributes()
    {
        var worker = new WorkerSnapshot(
            WorkerId: "44522",
            PreferredName: "Christopher",
            LastName: "Brien",
            Department: "Infrastructure & Security",
            TargetOu: "OU=Employees,DC=example,DC=com",
            IsPrehire: false,
            Attributes: new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
            {
                ["company"] = "Spire Services, Inc.",
                ["department"] = "Infrastructure & Security",
                ["employmentNav[0].jobInfoNav[0].companyNav.name_localized"] = "Spire Services, Inc.",
                ["employmentNav[0].jobInfoNav[0].locationNav.name"] = "STL - 700 Market",
                ["emptyValue"] = null
            });

        var planner = new WorkerPreviewPlanner(
            new StubWorkerSource(worker),
            new WorkerPlanningService(
                new StubDirectoryGateway(),
                new StubIdentityMatcher(),
                CreateLifecyclePolicy(),
                new StubAttributeDiffService(),
                new StubAttributeMappingProvider(),
                NullLogger<WorkerPlanningService>.Instance),
            new StubAttributeMappingProvider(),
            new StubWorkerPreviewLogWriter(),
            new StubRunRepository(),
            NullLogger<WorkerPreviewPlanner>.Instance);

        var preview = await planner.PreviewAsync("44522", CancellationToken.None);

        Assert.Contains(preview.SourceAttributes, attribute => attribute.Attribute == "company" && attribute.Value == "Spire Services, Inc.");
        Assert.Contains(preview.SourceAttributes, attribute => attribute.Attribute == "department" && attribute.Value == "Infrastructure & Security");
        Assert.Contains(preview.SourceAttributes, attribute => attribute.Attribute == "employmentNav[0].jobInfoNav[0].companyNav.name_localized" && attribute.Value == "Spire Services, Inc.");
        Assert.Contains(preview.SourceAttributes, attribute => attribute.Attribute == "employmentNav[0].jobInfoNav[0].locationNav.name" && attribute.Value == "STL - 700 Market");
        Assert.DoesNotContain(preview.SourceAttributes, attribute => attribute.Attribute == "emptyValue");
    }

    [Fact]
    public async Task PreviewAsync_PassesResolvedEmailAddressIntoAttributeDiffService()
    {
        var worker = new WorkerSnapshot(
            WorkerId: "44522",
            PreferredName: "Christopher",
            LastName: "Brien",
            Department: "Infrastructure & Security",
            TargetOu: "OU=Employees,DC=example,DC=com",
            IsPrehire: false,
            Attributes: new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase));

        var diffService = new CapturingAttributeDiffService();
        var planner = new WorkerPreviewPlanner(
            new StubWorkerSource(worker),
            new WorkerPlanningService(
                new StubDirectoryGateway(),
                new StubIdentityMatcher(),
                CreateLifecyclePolicy(),
                diffService,
                new StubAttributeMappingProvider(),
                NullLogger<WorkerPlanningService>.Instance),
            new StubAttributeMappingProvider(),
            new StubWorkerPreviewLogWriter(),
            new StubRunRepository(),
            NullLogger<WorkerPreviewPlanner>.Instance);

        await planner.PreviewAsync("44522", CancellationToken.None);

        Assert.Equal("christopher.brien@spireenergy.com", diffService.LastProposedEmailAddress);
    }

    [Fact]
    public async Task PreviewAsync_UsesExistingDirectoryUserWhenResolvingEmailLocalPart()
    {
        var worker = new WorkerSnapshot(
            WorkerId: "44522",
            PreferredName: "Christopher",
            LastName: "Brien",
            Department: "Infrastructure & Security",
            TargetOu: "OU=Employees,DC=example,DC=com",
            IsPrehire: false,
            Attributes: new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase));

        var directoryGateway = new ExistingUserEmailResolutionDirectoryGateway();
        var diffService = new CapturingAttributeDiffService();
        var planner = new WorkerPreviewPlanner(
            new StubWorkerSource(worker),
            new WorkerPlanningService(
                directoryGateway,
                new ExistingUserIdentityMatcher(),
                CreateLifecyclePolicy(),
                diffService,
                new StubAttributeMappingProvider(),
                NullLogger<WorkerPlanningService>.Instance),
            new StubAttributeMappingProvider(),
            new StubWorkerPreviewLogWriter(),
            new StubRunRepository(),
            NullLogger<WorkerPreviewPlanner>.Instance);

        await planner.PreviewAsync("44522", CancellationToken.None);

        Assert.Equal(1, directoryGateway.FindByWorkerCalls);
        Assert.Equal(0, directoryGateway.LegacyEmailLookupCalls);
        Assert.Same(directoryGateway.DirectoryUser, directoryGateway.LastExistingDirectoryUser);
        Assert.Equal("christopher.brien2@spireenergy.com", diffService.LastProposedEmailAddress);
    }

    [Fact]
    public async Task PreviewAsync_PersistsEmploymentStatusInSavedEntryItem()
    {
        var worker = new WorkerSnapshot(
            WorkerId: "44522",
            PreferredName: "Christopher",
            LastName: "Brien",
            Department: "Infrastructure & Security",
            TargetOu: "OU=Employees,DC=example,DC=com",
            IsPrehire: false,
            Attributes: new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
            {
                ["emplStatus"] = "64300"
            });

        var runRepository = new CapturingRunRepository();
        var planner = new WorkerPreviewPlanner(
            new StubWorkerSource(worker),
            new WorkerPlanningService(
                new StubDirectoryGateway(),
                new StubIdentityMatcher(),
                CreateLifecyclePolicy(),
                new StubAttributeDiffService(),
                new StubAttributeMappingProvider(),
                NullLogger<WorkerPlanningService>.Instance),
            new StubAttributeMappingProvider(),
            new StubWorkerPreviewLogWriter(),
            runRepository,
            NullLogger<WorkerPreviewPlanner>.Instance);

        await planner.PreviewAsync("44522", CancellationToken.None);

        var entry = runRepository.ReplacedEntries.Single().entries.Single();
        Assert.Equal("64300", entry.Item.GetProperty("emplStatus").GetString());
    }

    [Fact]
    public async Task PreviewAsync_TerminatedWorkerWithoutExistingUser_SkipsRequiredMappingReviewAndSyntheticDiffs()
    {
        var worker = new WorkerSnapshot(
            WorkerId: "44522",
            PreferredName: "Christopher",
            LastName: "Brien",
            Department: "Infrastructure & Security",
            TargetOu: "OU=Employees,DC=example,DC=com",
            IsPrehire: false,
            Attributes: new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
            {
                ["personIdExternal"] = "44522",
                ["lastName"] = "Brien",
                ["emplStatus"] = "T"
            });

        var diffService = new CapturingAttributeDiffService();
        var mappingProvider = new RequiredPathMappingProvider();
        var runRepository = new CapturingRunRepository();
        var planner = new WorkerPreviewPlanner(
            new StubWorkerSource(worker),
            new WorkerPlanningService(
                new StubDirectoryGateway(),
                new StubIdentityMatcher(),
                CreateLifecyclePolicy(),
                diffService,
                mappingProvider,
                NullLogger<WorkerPlanningService>.Instance),
            mappingProvider,
            new StubWorkerPreviewLogWriter(),
            runRepository,
            NullLogger<WorkerPreviewPlanner>.Instance);

        var preview = await planner.PreviewAsync("44522", CancellationToken.None);

        Assert.Equal("unchanged", preview.Buckets.Single());
        Assert.Null(preview.ReviewCaseType);
        Assert.Empty(preview.MissingSourceAttributes);
        Assert.DoesNotContain(preview.DiffRows, row => row.Changed);
        Assert.Null(diffService.LastProposedEmailAddress);
        Assert.Equal("unchanged", runRepository.ReplacedEntries.Single().entries.Single().Bucket);
        Assert.Empty(runRepository.ReplacedEntries.Single().entries.Single().Item.GetProperty("changedAttributeDetails").EnumerateArray());
    }

    [Fact]
    public async Task PreviewAsync_DoesNotRequireReviewWhenRequiredMappingsResolveThroughNormalizedSourcePaths()
    {
        var worker = new WorkerSnapshot(
            WorkerId: "44522",
            PreferredName: "Christopher",
            LastName: "Brien",
            Department: "Infrastructure & Security",
            TargetOu: "OU=Employees,DC=example,DC=com",
            IsPrehire: false,
            Attributes: new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
            {
                ["personIdExternal"] = "44522",
                ["firstName"] = "Christopher",
                ["lastName"] = "Brien",
                ["email"] = "christopher.brien@example.test"
            });

        var mappingProvider = new RequiredPathMappingProvider();
        var planner = new WorkerPreviewPlanner(
            new StubWorkerSource(worker),
            new WorkerPlanningService(
                new StubDirectoryGateway(),
                new StubIdentityMatcher(),
                CreateLifecyclePolicy(),
                new StubAttributeDiffService(),
                mappingProvider,
                NullLogger<WorkerPlanningService>.Instance),
            mappingProvider,
            new StubWorkerPreviewLogWriter(),
            new StubRunRepository(),
            NullLogger<WorkerPreviewPlanner>.Instance);

        var preview = await planner.PreviewAsync("44522", CancellationToken.None);

        Assert.Null(preview.ReviewCaseType);
        Assert.Empty(preview.MissingSourceAttributes);
    }

    [Fact]
    public async Task PreviewAsync_TruncatesMappedAttributeToAdSchemaLimit()
    {
        const string oversizedDepartment = "20921 MOW - Distribution - Maintenance & Construction - SW Missouri";
        var worker = new WorkerSnapshot(
            WorkerId: "20921",
            PreferredName: "Terry",
            LastName: "Example",
            Department: oversizedDepartment,
            TargetOu: "OU=Employees,DC=example,DC=com",
            IsPrehire: false,
            Attributes: new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
            {
                ["department"] = oversizedDepartment
            });

        var planner = new WorkerPreviewPlanner(
            new StubWorkerSource(worker),
            new WorkerPlanningService(
                new StubDirectoryGateway(),
                new StubIdentityMatcher(),
                CreateLifecyclePolicy(),
                new OversizedDepartmentDiffService(oversizedDepartment),
                new EmptyAttributeMappingProvider(),
                NullLogger<WorkerPlanningService>.Instance),
            new EmptyAttributeMappingProvider(),
            new StubWorkerPreviewLogWriter(),
            new StubRunRepository(),
            NullLogger<WorkerPreviewPlanner>.Instance);

        var preview = await planner.PreviewAsync("20921", CancellationToken.None);

        Assert.Equal("creates", preview.Buckets.Single());
        Assert.Null(preview.ReviewCategory);
        Assert.Null(preview.ReviewCaseType);
        Assert.Equal(
            oversizedDepartment[..64],
            preview.DiffRows.Single(row => row.Attribute == "department").After);
        Assert.Single(preview.Entries.Single().Item.GetProperty("operations").EnumerateArray());
    }

    [Fact]
    public async Task PreviewAsync_DoesNotRequireReviewWhenRequiredGivenNameResolvesFromPreferredNameFallback()
    {
        var worker = new WorkerSnapshot(
            WorkerId: "44522",
            PreferredName: "Terra",
            LastName: "Wells",
            Department: "Infrastructure & Security",
            TargetOu: "OU=Employees,DC=example,DC=com",
            IsPrehire: false,
            Attributes: new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
            {
                ["personIdExternal"] = "44522",
                ["lastName"] = "Wells"
            });

        var mappingProvider = new RequiredPathMappingProvider();
        var planner = new WorkerPreviewPlanner(
            new StubWorkerSource(worker),
            new WorkerPlanningService(
                new StubDirectoryGateway(),
                new StubIdentityMatcher(),
                CreateLifecyclePolicy(),
                new AttributeDiffService(mappingProvider, new StubWorkerPreviewLogWriter(), NullLogger<AttributeDiffService>.Instance),
                mappingProvider,
                NullLogger<WorkerPlanningService>.Instance),
            mappingProvider,
            new StubWorkerPreviewLogWriter(),
            new StubRunRepository(),
            NullLogger<WorkerPreviewPlanner>.Instance);

        var preview = await planner.PreviewAsync("44522", CancellationToken.None);

        Assert.Null(preview.ReviewCaseType);
        Assert.Empty(preview.MissingSourceAttributes);
        Assert.Equal("Terra", preview.DiffRows.Single(row => row.Attribute == "GivenName").After);
    }

    [Fact]
    public async Task PreviewAsync_DoesNotRequireReviewWhenRequiredUpnIsGeneratedFromResolvedEmail()
    {
        var worker = new WorkerSnapshot(
            WorkerId: "44522",
            PreferredName: "Christopher",
            LastName: "Brien",
            Department: "Infrastructure & Security",
            TargetOu: "OU=Employees,DC=example,DC=com",
            IsPrehire: false,
            Attributes: new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
            {
                ["personIdExternal"] = "44522",
                ["firstName"] = "Christopher",
                ["lastName"] = "Brien"
            });

        var mappingProvider = new RequiredGeneratedUpnMappingProvider();
        var planner = new WorkerPreviewPlanner(
            new StubWorkerSource(worker),
            new WorkerPlanningService(
                new StubDirectoryGateway(),
                new StubIdentityMatcher(),
                CreateLifecyclePolicy(),
                new AttributeDiffService(mappingProvider, new StubWorkerPreviewLogWriter(), NullLogger<AttributeDiffService>.Instance),
                mappingProvider,
                NullLogger<WorkerPlanningService>.Instance),
            mappingProvider,
            new StubWorkerPreviewLogWriter(),
            new StubRunRepository(),
            NullLogger<WorkerPreviewPlanner>.Instance);

        var preview = await planner.PreviewAsync("44522", CancellationToken.None);

        Assert.Null(preview.ReviewCaseType);
        Assert.Empty(preview.MissingSourceAttributes);
        Assert.Equal("christopher.brien@spireenergy.com", preview.DiffRows.Single(row => row.Attribute == "UserPrincipalName").After);
    }

    [Fact]
    public async Task PreviewAsync_DoesNotRequireReviewWhenManagerCannotBeResolved()
    {
        var worker = new WorkerSnapshot(
            WorkerId: "44522",
            PreferredName: "Christopher",
            LastName: "Brien",
            Department: "Infrastructure & Security",
            TargetOu: "OU=Employees,DC=example,DC=com",
            IsPrehire: false,
            Attributes: new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
            {
                ["company"] = "Spire Services, Inc.",
                ["department"] = "Infrastructure & Security",
                ["managerId"] = "10004"
            });

        var planner = new WorkerPreviewPlanner(
            new StubWorkerSource(worker),
            new WorkerPlanningService(
                new StubDirectoryGateway(),
                new StubIdentityMatcher(),
                CreateLifecyclePolicy(),
                new StubAttributeDiffService(),
                new StubAttributeMappingProvider(),
                NullLogger<WorkerPlanningService>.Instance),
            new StubAttributeMappingProvider(),
            new StubWorkerPreviewLogWriter(),
            new StubRunRepository(),
            NullLogger<WorkerPreviewPlanner>.Instance);

        var preview = await planner.PreviewAsync("44522", CancellationToken.None);

        Assert.Null(preview.ReviewCaseType);
        Assert.Null(preview.ReviewCategory);
    }

    [Fact]
    public async Task PreviewAsync_DoesNotRequireReviewWhenManagerLookupThrows()
    {
        var worker = new WorkerSnapshot(
            WorkerId: "44522",
            PreferredName: "Christopher",
            LastName: "Brien",
            Department: "Infrastructure & Security",
            TargetOu: "OU=Employees,DC=example,DC=com",
            IsPrehire: false,
            Attributes: new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
            {
                ["company"] = "Spire Services, Inc.",
                ["department"] = "Infrastructure & Security",
                ["managerId"] = "10004"
            });

        var planner = new WorkerPreviewPlanner(
            new StubWorkerSource(worker),
            new WorkerPlanningService(
                new ThrowingManagerDirectoryGateway(),
                new StubIdentityMatcher(),
                CreateLifecyclePolicy(),
                new StubAttributeDiffService(),
                new StubAttributeMappingProvider(),
                NullLogger<WorkerPlanningService>.Instance),
            new StubAttributeMappingProvider(),
            new StubWorkerPreviewLogWriter(),
            new StubRunRepository(),
            NullLogger<WorkerPreviewPlanner>.Instance);

        var preview = await planner.PreviewAsync("44522", CancellationToken.None);

        Assert.Null(preview.ReviewCaseType);
        Assert.Null(preview.ReviewCategory);
    }

    [Fact]
    public async Task PreviewAsync_ForExistingUsers_PreservesCurrentEmailTargets()
    {
        var worker = new WorkerSnapshot(
            WorkerId: "44522",
            PreferredName: "Christopher",
            LastName: "Brien",
            Department: "Infrastructure & Security",
            TargetOu: "OU=Employees,DC=example,DC=com",
            IsPrehire: false,
            Attributes: new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase));

        var planner = new WorkerPreviewPlanner(
            new StubWorkerSource(worker),
            new WorkerPlanningService(
                new ExistingUserDirectoryGateway(),
                new ExistingUserIdentityMatcher(),
                CreateLifecyclePolicy(),
                new AttributeDiffService(new EmptyAttributeMappingProvider(), new StubWorkerPreviewLogWriter(), NullLogger<AttributeDiffService>.Instance),
                new EmptyAttributeMappingProvider(),
                NullLogger<WorkerPlanningService>.Instance),
            new EmptyAttributeMappingProvider(),
            new StubWorkerPreviewLogWriter(),
            new StubRunRepository(),
            NullLogger<WorkerPreviewPlanner>.Instance);

        var preview = await planner.PreviewAsync("44522", CancellationToken.None);

        Assert.Equal("existing.upn@spireenergy.com", preview.DiffRows.Single(row => row.Attribute == "UserPrincipalName").After);
        Assert.False(preview.DiffRows.Single(row => row.Attribute == "UserPrincipalName").Changed);
        Assert.Equal("existing.mail@spireenergy.com", preview.DiffRows.Single(row => row.Attribute == "mail").After);
        Assert.False(preview.DiffRows.Single(row => row.Attribute == "mail").Changed);
    }

    [Fact]
    public async Task PreviewAsync_ForDisabledExistingUsersWithAttributeChanges_UsesUpdateBucket()
    {
        var worker = new WorkerSnapshot(
            WorkerId: "44522",
            PreferredName: "Christopher",
            LastName: "Brien",
            Department: "Infrastructure & Security",
            TargetOu: "OU=Employees,DC=example,DC=com",
            IsPrehire: false,
            Attributes: new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
            {
                ["company"] = "Spire Services, Inc.",
                ["department"] = "Infrastructure"
            });

        var planner = new WorkerPreviewPlanner(
            new StubWorkerSource(worker),
            new WorkerPlanningService(
                new DisabledExistingUserDirectoryGateway(),
                new ExistingUserIdentityMatcher(),
                CreateLifecyclePolicy(),
                new ChangedDepartmentDiffService(),
                new StubAttributeMappingProvider(),
                NullLogger<WorkerPlanningService>.Instance),
            new StubAttributeMappingProvider(),
            new StubWorkerPreviewLogWriter(),
            new StubRunRepository(),
            NullLogger<WorkerPreviewPlanner>.Instance);

        var preview = await planner.PreviewAsync("44522", CancellationToken.None);

        Assert.Equal("updates", preview.Buckets.Single());
        Assert.True(preview.DiffRows.Single(row => row.Attribute == "department").Changed);
        var operationKinds = preview.Entries
            .SelectMany(entry => entry.Item.GetProperty("operations").EnumerateArray())
            .Select(operation => operation.GetProperty("kind").GetString())
            .ToArray();
        Assert.Contains("UpdateUser", operationKinds);
        Assert.Contains("EnableUser", operationKinds);
    }

    [Fact]
    public async Task PreviewAsync_ForDisabledExistingUsersWithoutAttributeChanges_UsesEnableBucket()
    {
        var worker = new WorkerSnapshot(
            WorkerId: "44522",
            PreferredName: "Christopher",
            LastName: "Brien",
            Department: "Infrastructure & Security",
            TargetOu: "OU=Employees,DC=example,DC=com",
            IsPrehire: false,
            Attributes: new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
            {
                ["company"] = "Spire Services, Inc.",
                ["department"] = "Infrastructure & Security"
            });

        var planner = new WorkerPreviewPlanner(
            new StubWorkerSource(worker),
            new WorkerPlanningService(
                new DisabledExistingUserDirectoryGateway(),
                new ExistingUserIdentityMatcher(),
                CreateLifecyclePolicy(),
                new UnchangedAttributeDiffService(),
                new StubAttributeMappingProvider(),
                NullLogger<WorkerPlanningService>.Instance),
            new StubAttributeMappingProvider(),
            new StubWorkerPreviewLogWriter(),
            new StubRunRepository(),
            NullLogger<WorkerPreviewPlanner>.Instance);

        var preview = await planner.PreviewAsync("44522", CancellationToken.None);

        Assert.Equal("enables", preview.Buckets.Single());
        var enabledRow = preview.DiffRows.Single(row => row.Attribute == "enabled");
        Assert.True(enabledRow.Changed);
        Assert.Equal("false", enabledRow.Before);
        Assert.Equal("true", enabledRow.After);
    }

    [Fact]
    public async Task PreviewAsync_ForGraveyardUsersAlreadyDisabledWithoutAttributeChanges_UsesUnchangedBucket()
    {
        var worker = new WorkerSnapshot(
            WorkerId: "44522",
            PreferredName: "Christopher",
            LastName: "Brien",
            Department: "Infrastructure & Security",
            TargetOu: "OU=Employees,DC=example,DC=com",
            IsPrehire: false,
            Attributes: new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
            {
                ["company"] = "Spire Services, Inc.",
                ["department"] = "Infrastructure & Security",
                ["emplStatus"] = "T"
            });

        var runRepository = new CapturingRunRepository();
        var planner = new WorkerPreviewPlanner(
            new StubWorkerSource(worker),
            new WorkerPlanningService(
                new DisabledGraveyardUserDirectoryGateway(),
                new ExistingUserIdentityMatcher(),
                CreateLifecyclePolicy(),
                new UnchangedAttributeDiffService(),
                new StubAttributeMappingProvider(),
                NullLogger<WorkerPlanningService>.Instance),
            new StubAttributeMappingProvider(),
            new StubWorkerPreviewLogWriter(),
            runRepository,
            NullLogger<WorkerPreviewPlanner>.Instance);

        var preview = await planner.PreviewAsync("44522", CancellationToken.None);

        Assert.Equal("unchanged", preview.Buckets.Single());
        Assert.Empty(preview.Entries
            .SelectMany(entry => entry.Item.GetProperty("operations").EnumerateArray()));
        Assert.Equal("unchanged", runRepository.ReplacedEntries.Single().entries.Single().Bucket);
        Assert.Equal(1, runRepository.SavedRuns.Single().Unchanged);
        Assert.Equal(0, runRepository.SavedRuns.Single().Disables);
    }

    [Fact]
    public async Task PreviewAsync_ForGraveyardUsersAlreadyDisabledWithAttributeChanges_UsesUpdateBucket()
    {
        var worker = new WorkerSnapshot(
            WorkerId: "44522",
            PreferredName: "Christopher",
            LastName: "Brien",
            Department: "Infrastructure & Security",
            TargetOu: "OU=Employees,DC=example,DC=com",
            IsPrehire: false,
            Attributes: new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
            {
                ["company"] = "Spire Services, Inc.",
                ["department"] = "Infrastructure",
                ["emplStatus"] = "T"
            });

        var planner = new WorkerPreviewPlanner(
            new StubWorkerSource(worker),
            new WorkerPlanningService(
                new DisabledGraveyardUserDirectoryGateway(),
                new ExistingUserIdentityMatcher(),
                CreateLifecyclePolicy(),
                new ChangedDepartmentDiffService(),
                new StubAttributeMappingProvider(),
                NullLogger<WorkerPlanningService>.Instance),
            new StubAttributeMappingProvider(),
            new StubWorkerPreviewLogWriter(),
            new StubRunRepository(),
            NullLogger<WorkerPreviewPlanner>.Instance);

        var preview = await planner.PreviewAsync("44522", CancellationToken.None);

        Assert.Equal("updates", preview.Buckets.Single());
        Assert.True(preview.DiffRows.Single(row => row.Attribute == "department").Changed);
        var operationKinds = preview.Entries
            .SelectMany(entry => entry.Item.GetProperty("operations").EnumerateArray())
            .Select(operation => operation.GetProperty("kind").GetString())
            .ToArray();
        Assert.Contains("UpdateUser", operationKinds);
        Assert.DoesNotContain("DisableUser", operationKinds);
    }

    [Fact]
    public async Task PreviewAsync_WhenManagerChanges_IncludesManagerDiffAndPlansUpdate()
    {
        var worker = new WorkerSnapshot(
            WorkerId: "44522",
            PreferredName: "Christopher",
            LastName: "Brien",
            Department: "Infrastructure & Security",
            TargetOu: "OU=Employees,DC=example,DC=com",
            IsPrehire: false,
            Attributes: new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
            {
                ["company"] = "Spire Services, Inc.",
                ["department"] = "Infrastructure & Security",
                ["managerId"] = "10004"
            });

        var planner = new WorkerPreviewPlanner(
            new StubWorkerSource(worker),
            new WorkerPlanningService(
                new ExistingUserWithChangedManagerDirectoryGateway(),
                new ExistingUserIdentityMatcher(),
                CreateLifecyclePolicy(),
                new UnchangedAttributeDiffService(),
                new StubAttributeMappingProvider(),
                NullLogger<WorkerPlanningService>.Instance),
            new StubAttributeMappingProvider(),
            new StubWorkerPreviewLogWriter(),
            new StubRunRepository(),
            NullLogger<WorkerPreviewPlanner>.Instance);

        var preview = await planner.PreviewAsync("44522", CancellationToken.None);

        var managerRow = preview.DiffRows.Single(row => row.Attribute == "manager");
        Assert.True(managerRow.Changed);
        Assert.Equal("CN=Old Manager,OU=Employees,DC=example,DC=com", managerRow.Before);
        Assert.Equal("CN=New Manager,OU=Employees,DC=example,DC=com", managerRow.After);

        var operationKinds = preview.Entries
            .SelectMany(entry => entry.Item.GetProperty("operations").EnumerateArray())
            .Select(operation => operation.GetProperty("kind").GetString())
            .ToArray();
        Assert.Contains("UpdateUser", operationKinds);
    }

    private sealed class StubWorkerSource(WorkerSnapshot worker) : IWorkerSource
    {
        public Task<WorkerSnapshot?> GetWorkerAsync(string workerId, CancellationToken cancellationToken)
        {
            _ = workerId;
            _ = cancellationToken;
            return Task.FromResult<WorkerSnapshot?>(worker);
        }

        public async IAsyncEnumerable<WorkerSnapshot> ListWorkersAsync(WorkerListingMode mode, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
        {
            _ = mode;
            cancellationToken.ThrowIfCancellationRequested();
            yield return worker;
            await Task.Yield();
        }
    }

    private sealed class StubDirectoryGateway : IDirectoryGateway
    {
        public Task<DirectoryUserSnapshot?> FindByWorkerAsync(WorkerSnapshot worker, CancellationToken cancellationToken)
        {
            _ = worker;
            _ = cancellationToken;
            return Task.FromResult<DirectoryUserSnapshot?>(null);
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
            return Task.FromResult("christopher.brien");
        }
    }

    private sealed class StubIdentityMatcher : IIdentityMatcher
    {
        public IdentityMatchResult Match(WorkerSnapshot worker, DirectoryUserSnapshot? directoryUser)
        {
            _ = worker;
            _ = directoryUser;
            return new IdentityMatchResult(
                Bucket: "creates",
                MatchedExistingUser: false,
                SamAccountName: "44522",
                Reason: "Create preview",
                OperatorActionSummary: "Create account preview");
        }
    }

    private sealed class StubAttributeDiffService : IAttributeDiffService
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

    private sealed class CapturingAttributeDiffService : IAttributeDiffService
    {
        public string? LastProposedEmailAddress { get; private set; }

        public Task<IReadOnlyList<AttributeChange>> BuildDiffAsync(
            WorkerSnapshot worker,
            DirectoryUserSnapshot? directoryUser,
            string? proposedEmailAddress,
            string? logPath,
            CancellationToken cancellationToken)
        {
            _ = worker;
            _ = directoryUser;
            _ = logPath;
            _ = cancellationToken;
            LastProposedEmailAddress = proposedEmailAddress;
            return Task.FromResult<IReadOnlyList<AttributeChange>>([]);
        }
    }

    private sealed class UnchangedAttributeDiffService : IAttributeDiffService
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
            return Task.FromResult<IReadOnlyList<AttributeChange>>(
            [
                new AttributeChange("department", "department", "Infrastructure & Security", "Infrastructure & Security", false)
            ]);
        }
    }

    private sealed class StubWorkerPreviewLogWriter : IWorkerPreviewLogWriter
    {
        public string CreateLogPath(string workerId, DateTimeOffset startedAt)
        {
            return $"/tmp/{workerId}-{startedAt:yyyyMMddHHmmss}.json";
        }

        public Task AppendAsync(string logPath, WorkerPreviewLogEntry entry, CancellationToken cancellationToken)
        {
            _ = logPath;
            _ = entry;
            _ = cancellationToken;
            return Task.CompletedTask;
        }
    }

    private sealed class StubAttributeMappingProvider : IAttributeMappingProvider
    {
        public IReadOnlyList<AttributeMapping> GetEnabledMappings() =>
        [
            new AttributeMapping("company", "company", Required: true, Transform: "identity"),
            new AttributeMapping("department", "department", Required: true, Transform: "identity")
        ];
    }

    private sealed class RequiredPathMappingProvider : IAttributeMappingProvider
    {
        public IReadOnlyList<AttributeMapping> GetEnabledMappings() =>
        [
            new AttributeMapping("personIdExternal", "employeeID", Required: true, Transform: "Trim"),
            new AttributeMapping("personalInfoNav[0].firstName", "GivenName", Required: true, Transform: "Trim"),
            new AttributeMapping("personalInfoNav[0].lastName", "Surname", Required: true, Transform: "Trim"),
            new AttributeMapping("emailNav[?(@.isPrimary == true)].emailAddress", "UserPrincipalName", Required: true, Transform: "Lower")
        ];
    }

    private sealed class RequiredGeneratedUpnMappingProvider : IAttributeMappingProvider
    {
        public IReadOnlyList<AttributeMapping> GetEnabledMappings() =>
        [
            new AttributeMapping("emailNav[?(@.isPrimary == true)].emailAddress", "UserPrincipalName", Required: true, Transform: "Lower")
        ];
    }

    private sealed class ExistingUserIdentityMatcher : IIdentityMatcher
    {
        public IdentityMatchResult Match(WorkerSnapshot worker, DirectoryUserSnapshot? directoryUser)
        {
            _ = worker;
            _ = directoryUser;
            return new IdentityMatchResult(
                Bucket: "updates",
                MatchedExistingUser: true,
                SamAccountName: "cbrien",
                Reason: "Matched existing user",
                OperatorActionSummary: "Update account preview");
        }
    }

    private sealed class ExistingUserDirectoryGateway : IDirectoryGateway
    {
        public Task<DirectoryUserSnapshot?> FindByWorkerAsync(WorkerSnapshot worker, CancellationToken cancellationToken)
        {
            _ = worker;
            _ = cancellationToken;
            return Task.FromResult<DirectoryUserSnapshot?>(new DirectoryUserSnapshot(
                SamAccountName: "cbrien",
                DistinguishedName: "CN=Brien\\, Christopher,OU=Employees,DC=example,DC=com",
                Enabled: true,
                DisplayName: "Brien, Christopher",
                Attributes: new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
                {
                    ["UserPrincipalName"] = "existing.upn@spireenergy.com",
                    ["mail"] = "existing.mail@spireenergy.com"
                }));
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
            return Task.FromResult("christopher.brien2");
        }
    }

    private sealed class ExistingUserEmailResolutionDirectoryGateway : IDirectoryGateway
    {
        public DirectoryUserSnapshot DirectoryUser { get; } = new(
            SamAccountName: "cbrien",
            DistinguishedName: "CN=Brien\\, Christopher,OU=Employees,DC=example,DC=com",
            Enabled: true,
            DisplayName: "Brien, Christopher",
            Attributes: new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase));

        public int FindByWorkerCalls { get; private set; }
        public int LegacyEmailLookupCalls { get; private set; }
        public DirectoryUserSnapshot? LastExistingDirectoryUser { get; private set; }

        public Task<DirectoryUserSnapshot?> FindByWorkerAsync(WorkerSnapshot worker, CancellationToken cancellationToken)
        {
            _ = worker;
            _ = cancellationToken;
            FindByWorkerCalls++;
            return Task.FromResult<DirectoryUserSnapshot?>(DirectoryUser);
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
            LegacyEmailLookupCalls++;
            return Task.FromResult("legacy-path-should-not-run");
        }

        public Task<string> ResolveAvailableEmailLocalPartAsync(
            WorkerSnapshot worker,
            bool isCreate,
            DirectoryUserSnapshot? existingDirectoryUser,
            CancellationToken cancellationToken)
        {
            _ = worker;
            _ = isCreate;
            _ = cancellationToken;
            LastExistingDirectoryUser = existingDirectoryUser;
            return Task.FromResult("christopher.brien2");
        }
    }

    private sealed class ThrowingManagerDirectoryGateway : IDirectoryGateway
    {
        public Task<DirectoryUserSnapshot?> FindByWorkerAsync(WorkerSnapshot worker, CancellationToken cancellationToken)
        {
            _ = worker;
            _ = cancellationToken;
            return Task.FromResult<DirectoryUserSnapshot?>(null);
        }

        public Task<string?> ResolveManagerDistinguishedNameAsync(string managerId, CancellationToken cancellationToken)
        {
            _ = managerId;
            _ = cancellationToken;
            throw new InvalidOperationException("AD manager lookup failed.");
        }

        public Task<string> ResolveAvailableEmailLocalPartAsync(WorkerSnapshot worker, bool isCreate, CancellationToken cancellationToken)
        {
            _ = worker;
            _ = isCreate;
            _ = cancellationToken;
            return Task.FromResult("christopher.brien");
        }
    }

    private sealed class DisabledExistingUserDirectoryGateway : IDirectoryGateway
    {
        public Task<DirectoryUserSnapshot?> FindByWorkerAsync(WorkerSnapshot worker, CancellationToken cancellationToken)
        {
            _ = worker;
            _ = cancellationToken;
            return Task.FromResult<DirectoryUserSnapshot?>(new DirectoryUserSnapshot(
                SamAccountName: "cbrien",
                DistinguishedName: "CN=Brien\\, Christopher,OU=Employees,DC=example,DC=com",
                Enabled: false,
                DisplayName: "Brien, Christopher",
                Attributes: new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
                {
                    ["department"] = "Infrastructure & Security",
                    ["UserPrincipalName"] = "existing.upn@spireenergy.com",
                    ["mail"] = "existing.mail@spireenergy.com"
                }));
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
            return Task.FromResult("christopher.brien2");
        }
    }

    private sealed class DisabledGraveyardUserDirectoryGateway : IDirectoryGateway
    {
        public Task<DirectoryUserSnapshot?> FindByWorkerAsync(WorkerSnapshot worker, CancellationToken cancellationToken)
        {
            _ = worker;
            _ = cancellationToken;
            return Task.FromResult<DirectoryUserSnapshot?>(new DirectoryUserSnapshot(
                SamAccountName: "cbrien",
                DistinguishedName: "CN=Brien\\, Christopher,OU=Graveyard,DC=example,DC=com",
                Enabled: false,
                DisplayName: "Brien, Christopher",
                Attributes: new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
                {
                    ["department"] = "Infrastructure & Security",
                    ["UserPrincipalName"] = "existing.upn@spireenergy.com",
                    ["mail"] = "existing.mail@spireenergy.com"
                }));
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
            return Task.FromResult("christopher.brien2");
        }
    }

    private sealed class ExistingUserWithChangedManagerDirectoryGateway : IDirectoryGateway
    {
        public Task<DirectoryUserSnapshot?> FindByWorkerAsync(WorkerSnapshot worker, CancellationToken cancellationToken)
        {
            _ = worker;
            _ = cancellationToken;
            return Task.FromResult<DirectoryUserSnapshot?>(new DirectoryUserSnapshot(
                SamAccountName: "cbrien",
                DistinguishedName: "CN=Brien\\, Christopher,OU=Employees,DC=example,DC=com",
                Enabled: true,
                DisplayName: "Brien, Christopher",
                Attributes: new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
                {
                    ["department"] = "Infrastructure & Security",
                    ["UserPrincipalName"] = "existing.upn@spireenergy.com",
                    ["mail"] = "existing.mail@spireenergy.com",
                    ["manager"] = "CN=Old Manager,OU=Employees,DC=example,DC=com"
                }));
        }

        public Task<string?> ResolveManagerDistinguishedNameAsync(string managerId, CancellationToken cancellationToken)
        {
            _ = managerId;
            _ = cancellationToken;
            return Task.FromResult<string?>("CN=New Manager,OU=Employees,DC=example,DC=com");
        }

        public Task<string> ResolveAvailableEmailLocalPartAsync(WorkerSnapshot worker, bool isCreate, CancellationToken cancellationToken)
        {
            _ = worker;
            _ = isCreate;
            _ = cancellationToken;
            return Task.FromResult("christopher.brien2");
        }
    }

    private sealed class ChangedDepartmentDiffService : IAttributeDiffService
    {
        public Task<IReadOnlyList<AttributeChange>> BuildDiffAsync(
            WorkerSnapshot worker,
            DirectoryUserSnapshot? directoryUser,
            string? proposedEmailAddress,
            string? logPath,
            CancellationToken cancellationToken)
        {
            _ = worker;
            _ = proposedEmailAddress;
            _ = logPath;
            _ = cancellationToken;
            return Task.FromResult<IReadOnlyList<AttributeChange>>(
            [
                new AttributeChange(
                    "department",
                    "department",
                    directoryUser?.Attributes["department"] ?? "(unset)",
                    "Infrastructure",
                    true)
            ]);
        }
    }

    private sealed class OversizedDepartmentDiffService(string oversizedDepartment) : IAttributeDiffService
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
            return Task.FromResult<IReadOnlyList<AttributeChange>>(
            [
                new AttributeChange(
                    "department",
                    "Concat(costCenterId, costCenterDescription)",
                    "(unset)",
                    oversizedDepartment,
                    true)
            ]);
        }
    }

    private sealed class EmptyAttributeMappingProvider : IAttributeMappingProvider
    {
        public IReadOnlyList<AttributeMapping> GetEnabledMappings() => [];
    }

    private class CapturingRunRepository : IRunRepository
    {
        public List<RunRecord> SavedRuns { get; } = [];

        public List<(string runId, IReadOnlyList<RunEntryRecord> entries)> ReplacedEntries { get; } = [];

        public Task<IReadOnlyList<RunSummary>> ListRunsAsync(CancellationToken cancellationToken)
        {
            _ = cancellationToken;
            return Task.FromResult<IReadOnlyList<RunSummary>>([]);
        }

        public Task<RunDetail?> GetRunAsync(string runId, CancellationToken cancellationToken)
        {
            _ = runId;
            _ = cancellationToken;
            return Task.FromResult<RunDetail?>(null);
        }

        public Task<WorkerPreviewResult?> GetWorkerPreviewAsync(string runId, CancellationToken cancellationToken)
        {
            _ = runId;
            _ = cancellationToken;
            return Task.FromResult<WorkerPreviewResult?>(null);
        }

        public Task<IReadOnlyList<WorkerPreviewHistoryItem>> ListWorkerPreviewHistoryAsync(string workerId, int take, CancellationToken cancellationToken)
        {
            _ = workerId;
            _ = take;
            _ = cancellationToken;
            return Task.FromResult<IReadOnlyList<WorkerPreviewHistoryItem>>([]);
        }

        public Task SaveRunAsync(RunRecord run, CancellationToken cancellationToken)
        {
            _ = cancellationToken;
            SavedRuns.Add(run);
            return Task.CompletedTask;
        }

        public Task ReplaceRunEntriesAsync(string runId, IReadOnlyList<RunEntryRecord> entries, CancellationToken cancellationToken)
        {
            _ = cancellationToken;
            ReplacedEntries.Add((runId, entries));
            return Task.CompletedTask;
        }

        public Task AppendRunEntryAsync(RunEntryRecord entry, CancellationToken cancellationToken)
        {
            _ = entry;
            _ = cancellationToken;
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<RunEntry>> GetRunEntriesAsync(string runId, string? bucket, string? workerId, string? reason, string? filter, string? employmentStatus, string? entryId, int skip, int take, CancellationToken cancellationToken)
        {
            _ = runId;
            _ = bucket;
            _ = workerId;
            _ = reason;
            _ = filter;
            _ = employmentStatus;
            _ = entryId;
            _ = skip;
            _ = take;
            _ = cancellationToken;
            return Task.FromResult<IReadOnlyList<RunEntry>>([]);
        }

        public Task<int> CountRunEntriesAsync(string runId, string? bucket, string? workerId, string? reason, string? filter, string? employmentStatus, string? entryId, CancellationToken cancellationToken)
        {
            _ = runId;
            _ = bucket;
            _ = workerId;
            _ = reason;
            _ = filter;
            _ = employmentStatus;
            _ = entryId;
            _ = cancellationToken;
            return Task.FromResult(0);
        }

        public Task<IReadOnlyList<ChangedAttributeTotal>> GetRunEntryAttributeTotalsAsync(string runId, string? bucket, string? workerId, string? reason, string? filter, string? employmentStatus, string? entryId, CancellationToken cancellationToken)
        {
            _ = runId;
            _ = bucket;
            _ = workerId;
            _ = reason;
            _ = filter;
            _ = employmentStatus;
            _ = entryId;
            _ = cancellationToken;
            return Task.FromResult<IReadOnlyList<ChangedAttributeTotal>>([]);
        }
    }

    private sealed class StubRunRepository : CapturingRunRepository;

    private static LifecyclePolicy CreateLifecyclePolicy()
    {
        return new LifecyclePolicy(
            new LifecyclePolicySettings(
                ActiveOu: "OU=Employees,DC=example,DC=com",
                PrehireOu: "OU=Prehire,DC=example,DC=com",
                GraveyardOu: "OU=Graveyard,DC=example,DC=com",
                InactiveStatusField: "emplStatus",
                InactiveStatusValues: ["T"]));
    }
}

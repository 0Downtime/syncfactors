using Microsoft.Extensions.Options;
using SyncFactors.MockSuccessFactors;
using System.Text.Json;

namespace SyncFactors.MockSuccessFactors.Tests;

public sealed class MockFixtureStoreAdminTests
{
    private static readonly string FixturePath = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "config", "mock-successfactors", "baseline-fixtures.json"));

    [Fact]
    public void Store_SeedsRuntimeFile_OnFirstLoad()
    {
        var runtimePath = CreateRuntimePath();

        _ = CreateStore(runtimePath);

        Assert.True(File.Exists(runtimePath));
        var seededStore = CreateStore(runtimePath);
        Assert.Equal(10, seededStore.GetDocument().Workers.Count);
    }

    [Fact]
    public void Store_CreateUpdateDelete_PersistAcrossReload()
    {
        var runtimePath = CreateRuntimePath();
        var store = CreateStore(runtimePath);

        var created = store.CreateWorker(new MockAdminWorkerUpsertRequest(
            FirstName: "Ada",
            LastName: "Lovelace",
            StartDate: "2026-04-22",
            Department: "Engineering",
            Company: "CORP"));

        var updated = store.UpdateWorker(created.PersonIdExternal, new MockAdminWorkerUpsertRequest(
            PersonIdExternal: created.PersonIdExternal,
            FirstName: "Ada",
            LastName: "Byron",
            StartDate: "2026-04-22",
            Department: "Platform",
            Company: "CORP",
            EmploymentStatus: "A"));

        var reloaded = CreateStore(runtimePath);
        var persisted = reloaded.GetEditableWorker(updated.PersonIdExternal);

        Assert.NotNull(persisted);
        Assert.Equal("Byron", persisted!.LastName);
        Assert.Equal("Platform", persisted.Department);

        reloaded.DeleteWorker(updated.PersonIdExternal);
        var afterDelete = CreateStore(runtimePath);
        Assert.Null(afterDelete.GetEditableWorker(updated.PersonIdExternal));
    }

    [Fact]
    public void Store_ResetToSeed_RestoresOriginalPopulation()
    {
        var runtimePath = CreateRuntimePath();
        var store = CreateStore(runtimePath);
        var created = store.CreateWorker(new MockAdminWorkerUpsertRequest(
            FirstName: "Grace",
            LastName: "Hopper",
            StartDate: "2026-04-22"));

        Assert.NotNull(store.GetEditableWorker(created.PersonIdExternal));

        var count = store.ResetToSeed();
        var reloaded = CreateStore(runtimePath);

        Assert.Equal(10, count);
        Assert.Equal(10, reloaded.GetDocument().Workers.Count);
        Assert.Null(reloaded.GetEditableWorker(created.PersonIdExternal));
    }

    [Fact]
    public void Store_Create_AllocatesNextIdentity_AndDefaultsDerivedFields()
    {
        var runtimePath = CreateRuntimePath();
        var store = CreateStore(runtimePath);

        var created = store.CreateWorker(new MockAdminWorkerUpsertRequest(
            FirstName: "Terry",
            LastName: "Pratchett",
            StartDate: "2026-04-22"));

        Assert.Equal("40102", created.PersonIdExternal);
        Assert.Equal("40102", created.UserName);
        Assert.Equal("40102", created.UserId);
        Assert.Equal("terry.pratchett@example.test", created.Email);
        Assert.Equal("uuid-40102", created.PerPersonUuid);
    }

    [Fact]
    public void Store_Clone_AssignsUniqueEmail_WhenBaseAddressAlreadyExists()
    {
        var runtimePath = CreateRuntimePath();
        var store = CreateStore(runtimePath);

        var original = store.GetDocument().Workers[0];
        var cloned = store.CloneWorker(original.PersonIdExternal);
        var expectedBaseEmail = MockNameCatalog.BuildEmailAddress(original.FirstName, original.LastName);

        Assert.NotEqual(original.PersonIdExternal, cloned.PersonIdExternal);
        Assert.NotEqual(original.Email, cloned.Email);
        Assert.StartsWith(expectedBaseEmail.Replace("@example.test", string.Empty, StringComparison.Ordinal), cloned.Email, StringComparison.Ordinal);
        Assert.EndsWith("@example.test", cloned.Email, StringComparison.Ordinal);
    }

    [Fact]
    public void Store_CreateNameConflictWorker_CopiesNamesAndAllocatesUniqueSourceEmail()
    {
        var runtimePath = CreateRuntimePath();
        var store = CreateStore(runtimePath);

        var original = store.GetDocument().Workers[0];
        var created = store.CreateNameConflictWorker(original.PersonIdExternal);

        Assert.NotEqual(original.PersonIdExternal, created.PersonIdExternal);
        Assert.Equal(original.FirstName, created.FirstName);
        Assert.Equal(original.LastName, created.LastName);
        Assert.Equal(original.PreferredName, created.PreferredName);
        Assert.Equal(original.DisplayName, created.DisplayName);
        Assert.NotEqual(original.Email, created.Email);
        Assert.Equal("64300", created.EmploymentStatus);
        Assert.Equal(MockLifecycleState.Active, created.LifecycleState);
        Assert.Contains("name-conflict", created.ScenarioTags);
        Assert.Equal("1", created.ActiveEmploymentsCount);
    }

    [Fact]
    public void Store_SyntheticPopulationSeed_IsFrozenAfterRuntimeFileExists()
    {
        var runtimePath = CreateRuntimePath();
        var firstStore = CreateStore(runtimePath, syntheticPopulationEnabled: true, targetWorkerCount: 12);

        Assert.Equal(12, firstStore.GetDocument().Workers.Count);

        var secondStore = CreateStore(runtimePath, syntheticPopulationEnabled: true, targetWorkerCount: 25);
        Assert.Equal(12, secondStore.GetDocument().Workers.Count);
    }

    [Fact]
    public void Store_AdminState_IncludesProvisioningBucketsForWorkers()
    {
        var runtimePath = CreateRuntimePath();
        var store = CreateStore(runtimePath);

        var state = store.GetAdminState(filter: null, adminPath: "/admin");

        Assert.NotEmpty(state.ProvisioningBuckets);
        Assert.NotEmpty(state.Workers);
        Assert.All(
            state.Workers,
            worker =>
            {
                Assert.False(string.IsNullOrWhiteSpace(worker.ProvisioningBucket));
                Assert.False(string.IsNullOrWhiteSpace(worker.ProvisioningBucketLabel));
            });
        Assert.Equal(state.TotalWorkers, state.ProvisioningBuckets.Sum(bucket => bucket.Count));

        var payload = JsonSerializer.Serialize(state);
        Assert.Contains("\"provisioningBuckets\":", payload, StringComparison.Ordinal);
        Assert.Contains("\"provisioningBucket\":", payload, StringComparison.Ordinal);
    }

    [Fact]
    public void Store_AdminState_AppliesMultiTokenFilterAcrossSummaryFields()
    {
        var runtimePath = CreateRuntimePath();
        var store = CreateStore(runtimePath);

        var filtered = store.GetAdminState("casey engineering", "/admin");
        var empty = store.GetAdminState("casey missing-token", "/admin");

        Assert.Equal("/admin", filtered.AdminPath);
        Assert.True(filtered.TotalWorkers >= filtered.FilteredWorkers);
        Assert.All(
            filtered.Workers,
            worker =>
            {
                var text = string.Join(' ', worker.DisplayName, worker.Department, worker.Email);
                Assert.Contains("casey", text, StringComparison.OrdinalIgnoreCase);
                Assert.Contains("engineering", text, StringComparison.OrdinalIgnoreCase);
            });
        Assert.Equal(0, empty.FilteredWorkers);
    }

    [Fact]
    public void Store_FindByIdentity_UsesSupportedIdentityFields()
    {
        var runtimePath = CreateRuntimePath();
        var store = CreateStore(runtimePath);
        var worker = store.GetDocument().Workers[0];

        Assert.Null(store.FindByIdentity("personIdExternal", null));
        Assert.Equal(worker.PersonIdExternal, store.FindByIdentity("personIdExternal", worker.PersonIdExternal)?.PersonIdExternal);
        Assert.Equal(worker.PersonIdExternal, store.FindByIdentity("userId", worker.UserId)?.PersonIdExternal);
        Assert.Equal(worker.PersonIdExternal, store.FindByIdentity("userName", worker.UserName)?.PersonIdExternal);
        Assert.Null(store.FindByIdentity("email", worker.Email));
    }

    [Fact]
    public void Store_QueryWorkers_AppliesEmpJobStatusDateAndIdentityFilters()
    {
        var runtimePath = CreateRuntimePath();
        var store = CreateStore(runtimePath);
        var worker = store.GetDocument().Workers[0];
        var filteredQuery = Query(
            filter: "((emplStatus in '64300','64304') and startDate le datetime'2100-01-01T00:00:00Z') or emplStatus eq '64308'",
            asOfDate: "2100-01-01T00:00:00Z");
        var impossibleDateQuery = Query(
            filter: "startDate gt datetimeoffset'2999-01-01T00:00:00Z'",
            asOfDate: "2100-01-01T00:00:00Z");
        var identityQuery = Query(identityField: "userName", workerId: worker.UserName);

        var filtered = store.QueryWorkers("EmpJob", filteredQuery);
        var impossibleDate = store.QueryWorkers("EmpJob", impossibleDateQuery);
        var identity = store.QueryWorkers("PerPerson", identityQuery);

        Assert.NotEmpty(filtered);
        Assert.All(
            filtered,
            item => Assert.Contains(item.EmploymentStatus, ["64300", "64304", "64308"], StringComparer.OrdinalIgnoreCase));
        Assert.Empty(impossibleDate);
        Assert.Single(identity);
        Assert.Equal(worker.PersonIdExternal, identity[0].PersonIdExternal);
    }

    [Fact]
    public void Store_QueryWorkers_DefaultEmpJobListingCanExcludeTaggedPrehires()
    {
        var runtimePath = CreateRuntimePath();
        var store = CreateStore(runtimePath, includeTaggedPrehiresInDefaultListing: false);

        var workers = store.QueryWorkers("EmpJob", Query());

        Assert.DoesNotContain(
            workers,
            worker => worker.ScenarioTags.Contains("prehire", StringComparer.OrdinalIgnoreCase) &&
                      DateTimeOffset.TryParse(worker.StartDate, out var startDate) &&
                      startDate > DateTimeOffset.UtcNow);
    }

    [Fact]
    public void Store_UpdateWorker_RenamesManagerReferences_AndDeleteClearsThem()
    {
        var runtimePath = CreateRuntimePath();
        var store = CreateStore(runtimePath);
        var manager = store.CreateWorker(new MockAdminWorkerUpsertRequest(
            FirstName: "Maya",
            LastName: "Manager",
            StartDate: "2026-04-22"));
        var report = store.CreateWorker(new MockAdminWorkerUpsertRequest(
            FirstName: "Riley",
            LastName: "Report",
            StartDate: "2026-04-22",
            ManagerId: manager.PersonIdExternal));

        var renamedManager = store.UpdateWorker(manager.PersonIdExternal, ToRequest(manager) with
        {
            PersonIdExternal = "49999",
            UserName = "49999",
            UserId = "49999",
            Email = "maya.manager.49999@example.test"
        });
        var updatedReport = store.GetEditableWorker(report.PersonIdExternal);

        Assert.Equal("49999", renamedManager.PersonIdExternal);
        Assert.Equal("49999", updatedReport!.ManagerId);

        store.DeleteWorker(renamedManager.PersonIdExternal);

        Assert.Null(store.GetEditableWorker(report.PersonIdExternal)!.ManagerId);
    }

    [Fact]
    public void Store_CreateWorker_RejectsDuplicateIdentityFieldsAndInvalidManagers()
    {
        var runtimePath = CreateRuntimePath();
        var store = CreateStore(runtimePath);
        var existing = store.GetDocument().Workers[0];
        var valid = new MockAdminWorkerUpsertRequest(
            FirstName: "Valid",
            LastName: "Worker",
            StartDate: "2026-04-22");

        Assert.Throws<InvalidOperationException>(() => store.CreateWorker(valid with { PersonIdExternal = existing.PersonIdExternal }));
        Assert.Throws<InvalidOperationException>(() => store.CreateWorker(valid with { UserName = existing.UserName }));
        Assert.Throws<InvalidOperationException>(() => store.CreateWorker(valid with { UserId = existing.UserId ?? existing.UserName }));
        Assert.Throws<InvalidOperationException>(() => store.CreateWorker(valid with { Email = existing.Email }));
        Assert.Throws<InvalidOperationException>(() => store.CreateWorker(valid with { PersonIdExternal = "49998", ManagerId = "49998" }));
        Assert.Throws<InvalidOperationException>(() => store.CreateWorker(valid with { ManagerId = "does-not-exist" }));
    }

    [Fact]
    public void Store_CreateWorker_NormalizesWhitespaceOptionalValuesAndResponseControls()
    {
        var runtimePath = CreateRuntimePath();
        var store = CreateStore(runtimePath);

        var created = store.CreateWorker(new MockAdminWorkerUpsertRequest(
            FirstName: "  Morgan  ",
            LastName: "  Trimmed  ",
            StartDate: " 2026-04-22 ",
            Location: new MockAdminLocationInput("  HQ  ", " ", null, " 12345 ", null),
            ScenarioTags: [" tag ", "TAG", "other"],
            Response: new MockAdminResponseControlsInput(
                ForceUnauthorized: false,
                ForceNotFound: true,
                ForceMalformedPayload: false,
                ForceEmptyResults: false)));

        Assert.Equal("Morgan", created.FirstName);
        Assert.Equal("Trimmed", created.LastName);
        Assert.Equal("2026-04-22", created.StartDate);
        Assert.Equal("HQ", created.Location!.Name);
        Assert.Null(created.Location.Address);
        Assert.Equal("12345", created.Location.ZipCode);
        Assert.Equal(new[] { "tag", "other" }, created.ScenarioTags);
        Assert.NotNull(created.Response);
        Assert.True(created.Response!.ForceNotFound);
    }

    [Fact]
    public void Store_CreateWorker_RequiresNamesAndStartDate()
    {
        var runtimePath = CreateRuntimePath();
        var store = CreateStore(runtimePath);
        var valid = new MockAdminWorkerUpsertRequest(
            FirstName: "Valid",
            LastName: "Worker",
            StartDate: "2026-04-22");

        Assert.Throws<InvalidOperationException>(() => store.CreateWorker(valid with { FirstName = " " }));
        Assert.Throws<InvalidOperationException>(() => store.CreateWorker(valid with { LastName = null }));
        Assert.Throws<InvalidOperationException>(() => store.CreateWorker(valid with { StartDate = " " }));
    }

    [Fact]
    public void Store_SyntheticPopulation_RejectsNonPositiveTarget()
    {
        var runtimePath = CreateRuntimePath();

        Assert.Throws<InvalidOperationException>(() => CreateStore(runtimePath, syntheticPopulationEnabled: true, targetWorkerCount: 0));
    }

    [Theory]
    [InlineData("prehire", "64300", "preboarding", 1, null)]
    [InlineData("active-started", "64300", "active", 0, null)]
    [InlineData("paid-leave", "64304", "paid-leave", 0, null)]
    [InlineData("unpaid-leave", "64303", "unpaid-leave", 0, null)]
    [InlineData("returned-from-leave", "64300", "active", 0, null)]
    [InlineData("terminated", "64308", "terminated", 0, "today")]
    public void Store_ApplyLifecycleState_MutatesRuntimeWorker(
        string lifecycleState,
        string expectedStatus,
        string expectedLifecycleState,
        int expectedStartOffsetDays,
        string? expectedEndDate)
    {
        var runtimePath = CreateRuntimePath();
        var store = CreateStore(runtimePath);
        var today = DateTimeOffset.UtcNow.Date.ToString("yyyy-MM-dd");
        var created = store.CreateWorker(new MockAdminWorkerUpsertRequest(
            FirstName: "Lifecycle",
            LastName: "Worker",
            StartDate: today,
            EmploymentStatus: "A",
            ScenarioTags: ["custom-tag"]));

        var updated = store.ApplyLifecycleState(created.PersonIdExternal, lifecycleState);
        var editable = store.GetEditableWorker(created.PersonIdExternal);
        var expectedDate = DateTimeOffset.UtcNow.Date.AddDays(expectedStartOffsetDays).ToString("yyyy-MM-dd");

        Assert.Equal(expectedStatus, updated.EmploymentStatus);
        Assert.Equal(expectedLifecycleState, updated.LifecycleState);
        Assert.Equal(expectedDate, updated.StartDate);
        Assert.Equal(expectedEndDate == "today" ? today : null, updated.EndDate);
        Assert.NotNull(editable);
        Assert.Equal(expectedStatus, editable!.EmploymentStatus);
        Assert.Equal(expectedLifecycleState, editable.LifecycleState);
        Assert.Contains("custom-tag", updated.ScenarioTags);
        Assert.Equal(lifecycleState == "prehire", updated.ScenarioTags.Contains("prehire", StringComparer.OrdinalIgnoreCase));
    }

    private static MockFixtureStore CreateStore(
        string runtimePath,
        bool syntheticPopulationEnabled = false,
        int targetWorkerCount = 5000,
        bool includeTaggedPrehiresInDefaultListing = true)
    {
        return new MockFixtureStore(Options.Create(new MockSuccessFactorsOptions
        {
            FixturePath = FixturePath,
            EmpJob = new MockEmpJobOptions
            {
                IncludeTaggedPrehiresInDefaultListing = includeTaggedPrehiresInDefaultListing
            },
            SyntheticPopulation = new MockSyntheticPopulationOptions
            {
                Enabled = syntheticPopulationEnabled,
                TargetWorkerCount = targetWorkerCount
            },
            Runtime = new MockRuntimeOptions
            {
                FixturePath = runtimePath
            }
        }));
    }

    private static string CreateRuntimePath()
        => Path.Combine(Path.GetTempPath(), $"mock-successfactors-store-{Guid.NewGuid():N}.json");

    private static ODataQuery Query(
        string? filter = null,
        string identityField = "",
        string? workerId = null,
        string? asOfDate = null)
    {
        return new ODataQuery(
            IsSupported: true,
            ErrorMessage: null,
            Filter: filter,
            OrderBy: null,
            IdentityField: identityField,
            WorkerId: workerId,
            Top: null,
            Skip: 0,
            AsOfDate: asOfDate,
            Select: new HashSet<string>(StringComparer.OrdinalIgnoreCase),
            Expand: new HashSet<string>(StringComparer.OrdinalIgnoreCase));
    }

    private static MockAdminWorkerUpsertRequest ToRequest(MockWorkerFixture worker)
    {
        return new MockAdminWorkerUpsertRequest(
            PersonIdExternal: worker.PersonIdExternal,
            UserName: worker.UserName,
            Email: worker.Email,
            FirstName: worker.FirstName,
            LastName: worker.LastName,
            StartDate: worker.StartDate,
            Department: worker.Department,
            Company: worker.Company,
            Location: worker.Location is null
                ? null
                : new MockAdminLocationInput(
                    worker.Location.Name,
                    worker.Location.Address,
                    worker.Location.City,
                    worker.Location.ZipCode,
                    worker.Location.CustomString4),
            JobTitle: worker.JobTitle,
            BusinessUnit: worker.BusinessUnit,
            Division: worker.Division,
            CostCenter: worker.CostCenter,
            EmployeeClass: worker.EmployeeClass,
            EmployeeType: worker.EmployeeType,
            ManagerId: worker.ManagerId,
            PeopleGroup: worker.PeopleGroup,
            LeadershipLevel: worker.LeadershipLevel,
            Region: worker.Region,
            Geozone: worker.Geozone,
            BargainingUnit: worker.BargainingUnit,
            UnionJobCode: worker.UnionJobCode,
            EmploymentStatus: worker.EmploymentStatus,
            LifecycleState: worker.LifecycleState,
            EndDate: worker.EndDate,
            FirstDateWorked: worker.FirstDateWorked,
            LastDateWorked: worker.LastDateWorked,
            IsContingentWorker: worker.IsContingentWorker,
            LastModifiedDateTime: worker.LastModifiedDateTime,
            ScenarioTags: worker.ScenarioTags,
            PersonId: worker.PersonId,
            PerPersonUuid: worker.PerPersonUuid,
            PreferredName: worker.PreferredName,
            DisplayName: worker.DisplayName,
            UserId: worker.UserId,
            EmailType: worker.EmailType,
            DepartmentName: worker.DepartmentName,
            DepartmentId: worker.DepartmentId,
            DepartmentCostCenter: worker.DepartmentCostCenter,
            CompanyId: worker.CompanyId,
            BusinessUnitId: worker.BusinessUnitId,
            DivisionId: worker.DivisionId,
            CostCenterDescription: worker.CostCenterDescription,
            CostCenterId: worker.CostCenterId,
            TwoCharCountryCode: worker.TwoCharCountryCode,
            Position: worker.Position,
            PayGrade: worker.PayGrade,
            BusinessPhoneNumber: worker.BusinessPhoneNumber,
            BusinessPhoneAreaCode: worker.BusinessPhoneAreaCode,
            BusinessPhoneCountryCode: worker.BusinessPhoneCountryCode,
            BusinessPhoneExtension: worker.BusinessPhoneExtension,
            CellPhoneNumber: worker.CellPhoneNumber,
            CellPhoneAreaCode: worker.CellPhoneAreaCode,
            CellPhoneCountryCode: worker.CellPhoneCountryCode,
            ActiveEmploymentsCount: worker.ActiveEmploymentsCount,
            LatestTerminationDate: worker.LatestTerminationDate);
    }
}

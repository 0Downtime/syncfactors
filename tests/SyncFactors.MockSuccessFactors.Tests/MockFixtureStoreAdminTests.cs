using Microsoft.Extensions.Options;
using SyncFactors.MockSuccessFactors;

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
    public void Store_SyntheticPopulationSeed_IsFrozenAfterRuntimeFileExists()
    {
        var runtimePath = CreateRuntimePath();
        var firstStore = CreateStore(runtimePath, syntheticPopulationEnabled: true, targetWorkerCount: 12);

        Assert.Equal(12, firstStore.GetDocument().Workers.Count);

        var secondStore = CreateStore(runtimePath, syntheticPopulationEnabled: true, targetWorkerCount: 25);
        Assert.Equal(12, secondStore.GetDocument().Workers.Count);
    }

    private static MockFixtureStore CreateStore(
        string runtimePath,
        bool syntheticPopulationEnabled = false,
        int targetWorkerCount = 5000)
    {
        return new MockFixtureStore(Options.Create(new MockSuccessFactorsOptions
        {
            FixturePath = FixturePath,
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
}

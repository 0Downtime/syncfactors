using SyncFactors.Contracts;
using SyncFactors.Domain;
using SyncFactors.Infrastructure;

namespace SyncFactors.Infrastructure.Tests;

public sealed class RuntimeFixtureQueueSimulationTests
{
    [Fact]
    public async Task RuntimeFixtureDirectoryGateway_ProjectsTerminatedWorkersIntoGraveyardOu()
    {
        var fixturePath = Path.Combine(Path.GetTempPath(), $"syncfactors-runtime-fixture-{Guid.NewGuid():N}.json");
        await File.WriteAllTextAsync(
            fixturePath,
            """
            {
              "workers": [
                {
                  "personIdExternal": "10001",
                  "userId": "graveyard.user",
                  "firstName": "Graveyard",
                  "lastName": "User",
                  "displayName": "Graveyard User",
                  "employmentStatus": "T",
                  "lifecycleState": "terminated",
                  "endDate": "2026-01-15"
                },
                {
                  "personIdExternal": "10002",
                  "userId": "active.user",
                  "firstName": "Active",
                  "lastName": "User",
                  "displayName": "Active User",
                  "employmentStatus": "A",
                  "lifecycleState": "active",
                  "startDate": "2026-01-01"
                }
              ]
            }
            """);

        try
        {
            var gateway = new RuntimeFixtureDirectoryGateway(
                new MockRuntimeFixtureReader(new MockRuntimeFixturePathResolver(fixturePath)),
                CreateLifecycleSettings(),
                new FakeTimeProvider(DateTimeOffset.Parse("2026-04-11T12:00:00Z")));

            var user = await gateway.FindByWorkerAsync(CreateWorker("10001"), CancellationToken.None);
            var graveyardUsers = await gateway.ListUsersInOuAsync("OU=Graveyard,DC=example,DC=com", CancellationToken.None);

            Assert.NotNull(user);
            Assert.Equal("graveyard.user", user!.SamAccountName);
            Assert.Equal("CN=Graveyard User,OU=Graveyard,DC=example,DC=com", user.DistinguishedName);
            Assert.False(user.Enabled);
            Assert.Single(graveyardUsers);
            Assert.Equal("graveyard.user", graveyardUsers[0].SamAccountName);
        }
        finally
        {
            File.Delete(fixturePath);
        }
    }

    [Fact]
    public async Task RuntimeFixtureGraveyardRetentionStore_SynthesizesQueueRecords_AndPersistsHoldMetadata()
    {
        var fixturePath = Path.Combine(Path.GetTempPath(), $"syncfactors-runtime-fixture-{Guid.NewGuid():N}.json");
        var databasePath = Path.Combine(Path.GetTempPath(), $"syncfactors-runtime-fixture-{Guid.NewGuid():N}.db");
        await File.WriteAllTextAsync(
            fixturePath,
            """
            {
              "workers": [
                {
                  "personIdExternal": "10001",
                  "userId": "graveyard.user",
                  "firstName": "Graveyard",
                  "lastName": "User",
                  "displayName": "Graveyard User",
                  "employmentStatus": "T",
                  "lifecycleState": "terminated",
                  "endDate": "2026-01-15"
                },
                {
                  "personIdExternal": "10002",
                  "userId": "active.user",
                  "firstName": "Active",
                  "lastName": "User",
                  "displayName": "Active User",
                  "employmentStatus": "A",
                  "lifecycleState": "active",
                  "startDate": "2026-01-01"
                }
              ]
            }
            """);

        try
        {
            var now = DateTimeOffset.Parse("2026-04-11T12:00:00Z");
            var sqliteResolver = new SqlitePathResolver(databasePath);
            var sqliteStore = new SqliteGraveyardRetentionStore(sqliteResolver);
            var store = new RuntimeFixtureGraveyardRetentionStore(
                new MockRuntimeFixtureReader(new MockRuntimeFixturePathResolver(fixturePath)),
                sqliteStore,
                CreateLifecycleSettings(),
                new FakeTimeProvider(now));

            await new SqliteDatabaseInitializer(sqliteResolver).InitializeAsync(CancellationToken.None);
            await store.SetHoldAsync("graveyard.user", true, "admin-1", now, CancellationToken.None);

            var records = await store.ListActiveAsync(CancellationToken.None);

            var record = Assert.Single(records);
            Assert.Equal("graveyard.user", record.WorkerId);
            Assert.Equal("Graveyard User", record.DisplayName);
            Assert.Equal("CN=Graveyard User,OU=Graveyard,DC=example,DC=com", record.DistinguishedName);
            Assert.Equal(DateTimeOffset.Parse("2026-01-15T00:00:00+00:00"), record.EndDateUtc);
            Assert.True(record.IsOnHold);
            Assert.Equal(now, record.HoldPlacedAtUtc);
            Assert.Equal("admin-1", record.HoldPlacedBy);
        }
        finally
        {
            File.Delete(fixturePath);
            File.Delete(databasePath);
        }
    }

    private static LifecyclePolicySettings CreateLifecycleSettings() =>
        new(
            ActiveOu: "OU=Employees,DC=example,DC=com",
            PrehireOu: "OU=Prehire,DC=example,DC=com",
            GraveyardOu: "OU=Graveyard,DC=example,DC=com",
            InactiveStatusField: "emplStatus",
            InactiveStatusValues: ["T"],
            DirectoryIdentityAttribute: "sAMAccountName");

    private static WorkerSnapshot CreateWorker(string workerId) =>
        new(
            WorkerId: workerId,
            PreferredName: "Test",
            LastName: "Worker",
            Department: "IT",
            TargetOu: "OU=Employees,DC=example,DC=com",
            IsPrehire: false,
            Attributes: new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase));

    private sealed class FakeTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}

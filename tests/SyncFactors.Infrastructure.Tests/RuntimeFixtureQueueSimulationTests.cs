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

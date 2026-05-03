using SyncFactors.Contracts;
using SyncFactors.Infrastructure;

namespace SyncFactors.Infrastructure.Tests;

public sealed class ScaffoldGatewayTests
{
    [Fact]
    public async Task ScaffoldDirectoryGateway_LoadsDefaultDataAndResolvesUsers()
    {
        var scaffoldPath = Path.Combine(Path.GetTempPath(), $"syncfactors-scaffold-{Guid.NewGuid():N}.json");

        try
        {
            var store = new ScaffoldDataStore(new ScaffoldDataPathResolver(scaffoldPath));
            var gateway = new ScaffoldDirectoryGateway(store);
            var existingWorker = new WorkerSnapshot(
                WorkerId: "existing-2000456",
                PreferredName: "Existing",
                LastName: "Worker456",
                Department: "Platform",
                TargetOu: "OU=Platform,DC=example,DC=com",
                IsPrehire: false,
                Attributes: new Dictionary<string, string?>
                {
                    ["email"] = "existing.worker456@example.test",
                    ["emplStatus"] = "A"
                });

            var found = await gateway.FindByWorkerAsync(existingWorker, CancellationToken.None);
            var usersInOu = await gateway.ListUsersInOuAsync("OU=Platform,DC=example,DC=com", CancellationToken.None);
            var availableLocalPart = await gateway.ResolveAvailableEmailLocalPartAsync(existingWorker, isCreate: true, CancellationToken.None);
            var managerDn = await gateway.ResolveManagerDistinguishedNameAsync("90001", CancellationToken.None);

            Assert.True(File.Exists(scaffoldPath));
            Assert.NotNull(found);
            Assert.Equal("existing.worker456", found!.SamAccountName);
            Assert.Equal("CN=Existing Worker456,OU=Platform,DC=example,DC=com", found.DistinguishedName);
            Assert.Single(usersInOu);
            Assert.Equal("existing.worker456", usersInOu[0].SamAccountName);
            Assert.Equal("existing.worker456", availableLocalPart);
            Assert.Null(managerDn);
        }
        finally
        {
            File.Delete(scaffoldPath);
        }
    }

    [Fact]
    public async Task ScaffoldDirectoryCommandGateway_ReturnsSuccessfulCommandResult()
    {
        var gateway = new ScaffoldDirectoryCommandGateway();
        var command = new DirectoryMutationCommand(
            Action: "CreateUser",
            WorkerId: "10001",
            ManagerId: "90001",
            ManagerDistinguishedName: "CN=Manager,OU=Users,DC=example,DC=com",
            SamAccountName: "lab10001",
            CommonName: "Lab Worker",
            UserPrincipalName: "lab10001@example.test",
            Mail: "lab10001@example.test",
            TargetOu: "OU=Users,DC=example,DC=com",
            DisplayName: "Lab Worker",
            CurrentDistinguishedName: null,
            EnableAccount: true,
            Operations: [new DirectoryOperation("CreateUser")],
            Attributes: new Dictionary<string, string?> { ["displayName"] = "Lab Worker" });

        var result = await gateway.ExecuteAsync(command, CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.Equal("CreateUser", result.Action);
        Assert.Equal("lab10001", result.SamAccountName);
        Assert.Equal("CN=Lab Worker,OU=Users,DC=example,DC=com", result.DistinguishedName);
        Assert.Equal("Scaffold CreateUser completed for lab10001.", result.Message);
        Assert.Null(result.RunId);
    }

    [Fact]
    public void MockRuntimeFixturePathResolver_UsesConfiguredPathOrRepoDefault()
    {
        var configuredPath = Path.Combine(".", "state", "custom-runtime.json");

        var configured = new MockRuntimeFixturePathResolver(configuredPath).Resolve();
        var fallback = new MockRuntimeFixturePathResolver(" ").Resolve();

        Assert.Equal(Path.GetFullPath(configuredPath), configured);
        Assert.EndsWith(Path.Combine("state", "runtime", "mock-successfactors.runtime-fixtures.json"), fallback, StringComparison.Ordinal);
        Assert.True(Path.IsPathFullyQualified(fallback));
    }
}

using SyncFactors.Automation;
using SyncFactors.Infrastructure;

namespace SyncFactors.Automation.Tests;

public sealed class LocalAutomationUserBootstrapCommandTests
{
    [Fact]
    public async Task RunAsync_CreatesAndUpdatesLocalAutomationUser()
    {
        var databasePath = Path.Combine(Path.GetTempPath(), $"syncfactors-automation-bootstrap-{Guid.NewGuid():N}.db");
        try
        {
            var createExitCode = await LocalAutomationUserBootstrapCommand.RunAsync(
            [
                "--sqlite", databasePath,
                "--username", "syncfactors-automation",
                "--password", "Password12345"
            ], TextWriter.Null, CancellationToken.None);

            var promoteExitCode = await LocalAutomationUserBootstrapCommand.RunAsync(
            [
                "--sqlite", databasePath,
                "--username", "syncfactors-automation",
                "--password", "Password67890",
                "--admin"
            ], TextWriter.Null, CancellationToken.None);

            var store = new SqliteLocalUserStore(new SqlitePathResolver(databasePath));
            var user = await store.FindByUsernameAsync("syncfactors-automation", CancellationToken.None);

            Assert.Equal(0, createExitCode);
            Assert.Equal(0, promoteExitCode);
            Assert.NotNull(user);
            Assert.Equal(SecurityRoles.Admin, user!.Role);
            Assert.True(user.IsActive);
        }
        finally
        {
            File.Delete(databasePath);
        }
    }
}

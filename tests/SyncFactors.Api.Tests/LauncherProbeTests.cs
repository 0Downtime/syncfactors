using SyncFactors.Api;
using SyncFactors.Infrastructure;

namespace SyncFactors.Api.Tests;

public sealed class LauncherProbeTests
{
    [Fact]
    public async Task IsBootstrapRequiredAsync_ReturnsFalseWhenLocalAuthIsDisabled()
    {
        var options = new LocalAuthOptions
        {
            Mode = "oidc",
            LocalBreakGlass = new LocalBreakGlassOptions
            {
                Enabled = false
            },
            BootstrapAdmin = new BootstrapAdminOptions
            {
                Username = "admin"
            }
        };

        var required = await LauncherProbe.IsBootstrapRequiredAsync(options, new StubLocalUserStore(hasUsers: false), CancellationToken.None);

        Assert.False(required);
    }

    [Fact]
    public async Task IsBootstrapRequiredAsync_ReturnsTrueWhenHybridModeHasNoLocalUsers()
    {
        var options = new LocalAuthOptions
        {
            Mode = "hybrid",
            BootstrapAdmin = new BootstrapAdminOptions
            {
                Username = "admin"
            }
        };

        var required = await LauncherProbe.IsBootstrapRequiredAsync(options, new StubLocalUserStore(hasUsers: false), CancellationToken.None);

        Assert.True(required);
    }

    [Fact]
    public async Task IsBootstrapRequiredAsync_ReturnsFalseWhenHybridModeAlreadyHasLocalUsers()
    {
        var options = new LocalAuthOptions
        {
            Mode = "hybrid",
            BootstrapAdmin = new BootstrapAdminOptions
            {
                Username = "admin"
            }
        };

        var required = await LauncherProbe.IsBootstrapRequiredAsync(options, new StubLocalUserStore(hasUsers: true), CancellationToken.None);

        Assert.False(required);
    }

    [Fact]
    public async Task IsBootstrapRequiredAsync_ReturnsFalseWhenBootstrapUsernameIsMissing()
    {
        var options = new LocalAuthOptions
        {
            Mode = "hybrid",
            BootstrapAdmin = new BootstrapAdminOptions
            {
                Username = ""
            }
        };

        var required = await LauncherProbe.IsBootstrapRequiredAsync(options, new StubLocalUserStore(hasUsers: false), CancellationToken.None);

        Assert.False(required);
    }

    private sealed class StubLocalUserStore(bool hasUsers) : ILocalUserStore
    {
        public Task<bool> AnyUsersAsync(CancellationToken cancellationToken)
        {
            _ = cancellationToken;
            return Task.FromResult(hasUsers);
        }

        public Task<IReadOnlyList<LocalUserSummary>> ListUsersAsync(CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<LocalUserRecord?> FindByIdAsync(string userId, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<LocalUserRecord?> FindByUsernameAsync(string username, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<int> CountActiveAdminsAsync(CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task CreateAsync(LocalUserRecord user, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task UpdateAsync(LocalUserRecord user, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task UpdateLastLoginAsync(string userId, DateTimeOffset lastLoginAt, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task DeleteAsync(string userId, CancellationToken cancellationToken) => throw new NotSupportedException();
    }
}

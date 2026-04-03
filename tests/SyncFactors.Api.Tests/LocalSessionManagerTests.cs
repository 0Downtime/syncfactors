using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using SyncFactors.Api;
using SyncFactors.Infrastructure;

namespace SyncFactors.Api.Tests;

public sealed class LocalSessionManagerTests
{
    [Fact]
    public async Task BuildSessionResponseAsync_ReturnsAnonymous_WhenUserIsNotAuthenticated()
    {
        var httpContext = new DefaultHttpContext();
        var authService = new StubLocalAuthService(null);

        var session = await LocalSessionManager.BuildSessionResponseAsync(httpContext, authService, CancellationToken.None);

        Assert.False(session.IsAuthenticated);
        Assert.Null(session.Username);
        Assert.False(session.IsAdmin);
    }

    [Fact]
    public async Task BuildSessionResponseAsync_ReturnsCurrentUser_WhenCookieIdentityIsValid()
    {
        var user = new LocalUserRecord(
            UserId: "user-1",
            Username: "operator",
            NormalizedUsername: "OPERATOR",
            PasswordHash: "hash",
            Role: "Admin",
            IsActive: true,
            CreatedAt: DateTimeOffset.UtcNow,
            UpdatedAt: DateTimeOffset.UtcNow,
            LastLoginAt: null,
            FailedLoginCount: 0,
            LockoutEndAt: null);
        var httpContext = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(
                new ClaimsIdentity(
                [
                    new Claim(ClaimTypes.NameIdentifier, user.UserId),
                    new Claim(ClaimTypes.Name, user.Username),
                    new Claim(ClaimTypes.Role, user.Role),
                    new Claim(SecurityClaimTypes.AuthSource, "local")
                ],
                "Cookies"))
        };

        var session = await LocalSessionManager.BuildSessionResponseAsync(httpContext, new StubLocalAuthService(user), CancellationToken.None);

        Assert.True(session.IsAuthenticated);
        Assert.Equal("user-1", session.UserId);
        Assert.Equal("operator", session.Username);
        Assert.Equal("Admin", session.Role);
        Assert.True(session.IsAdmin);
    }

    [Fact]
    public async Task BuildSessionResponseAsync_UsesClaimsForOidcUsers()
    {
        var httpContext = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(
                new ClaimsIdentity(
                [
                    new Claim(ClaimTypes.NameIdentifier, "oidc-subject"),
                    new Claim(ClaimTypes.Name, "oidc-user"),
                    new Claim(ClaimTypes.Role, SecurityRoles.Operator),
                    new Claim(SecurityClaimTypes.AuthSource, "oidc")
                ],
                "Cookies"))
        };

        var session = await LocalSessionManager.BuildSessionResponseAsync(httpContext, new StubLocalAuthService(null), CancellationToken.None);

        Assert.True(session.IsAuthenticated);
        Assert.Equal("oidc-subject", session.UserId);
        Assert.Equal("oidc-user", session.Username);
        Assert.Equal(SecurityRoles.Operator, session.Role);
        Assert.False(session.IsAdmin);
    }

    private sealed class StubLocalAuthService(LocalUserRecord? user) : ILocalAuthService
    {
        public Task EnsureBootstrapAdminAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public Task<LocalAuthenticationResult> AuthenticateAsync(string username, string password, CancellationToken cancellationToken) =>
            Task.FromResult(LocalAuthenticationResult.Failed);

        public Task RecordSuccessfulLoginAsync(string userId, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task<IReadOnlyList<LocalUserSummary>> ListUsersAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<LocalUserSummary>>([]);

        public Task<LocalUserRecord?> FindUserByIdAsync(string userId, CancellationToken cancellationToken) =>
            Task.FromResult(user);

        public Task<LocalUserCommandResult> CreateUserAsync(string username, string password, bool isAdmin, CancellationToken cancellationToken) =>
            Task.FromResult(LocalUserCommandResult.Success("created"));

        public Task<LocalUserCommandResult> ResetPasswordAsync(string userId, string newPassword, CancellationToken cancellationToken) =>
            Task.FromResult(LocalUserCommandResult.Success("reset"));

        public Task<LocalUserCommandResult> SetUserRoleAsync(string userId, bool isAdmin, string actingUserId, CancellationToken cancellationToken) =>
            Task.FromResult(LocalUserCommandResult.Success("role"));

        public Task<LocalUserCommandResult> SetUserActiveStateAsync(string userId, bool isActive, string actingUserId, CancellationToken cancellationToken) =>
            Task.FromResult(LocalUserCommandResult.Success("active"));

        public Task<LocalUserCommandResult> DeleteUserAsync(string userId, string actingUserId, CancellationToken cancellationToken) =>
            Task.FromResult(LocalUserCommandResult.Success("deleted"));
    }
}

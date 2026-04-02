using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using SyncFactors.Api.Pages.Admin;
using SyncFactors.Infrastructure;

namespace SyncFactors.Api.Tests;

public sealed class AdminUsersModelTests
{
    [Fact]
    public async Task OnGetAsync_LoadsUsers()
    {
        var service = new StubAdminAuthService
        {
            Users =
            [
                new LocalUserSummary("user-1", "alice", "Operator", true, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, null)
            ]
        };
        var model = CreateModel(service);

        await model.OnGetAsync(CancellationToken.None);

        Assert.Single(model.Users);
        Assert.Equal("alice", model.Users[0].Username);
    }

    [Fact]
    public async Task OnPostCreateAsync_CreatesUserWhenPasswordsMatch()
    {
        var service = new StubAdminAuthService();
        var model = CreateModel(service);
        model.CreateUsername = "alice";
        model.CreatePassword = "Password1234";
        model.CreatePasswordConfirmation = "Password1234";
        model.CreateIsAdmin = true;

        var result = await model.OnPostCreateAsync(CancellationToken.None);

        Assert.IsType<RedirectToPageResult>(result);
        Assert.Equal("alice", service.LastCreateUsername);
        Assert.True(service.LastCreateIsAdmin);
        Assert.Equal("created", model.SuccessMessage);
    }

    [Fact]
    public async Task OnPostResetPasswordAsync_RejectsMismatchedConfirmation()
    {
        var service = new StubAdminAuthService();
        var model = CreateModel(service);

        var result = await model.OnPostResetPasswordAsync("user-2", "Password1234", "different", CancellationToken.None);

        Assert.IsType<RedirectToPageResult>(result);
        Assert.Equal("Reset password and confirmation must match.", model.ErrorMessage);
        Assert.Null(service.LastResetUserId);
    }

    [Fact]
    public async Task OnPostDeleteAsync_UsesCurrentUserAsActor()
    {
        var service = new StubAdminAuthService();
        var model = CreateModel(service, "admin-1");

        var result = await model.OnPostDeleteAsync("user-2", CancellationToken.None);

        Assert.IsType<RedirectToPageResult>(result);
        Assert.Equal("user-2", service.LastDeleteUserId);
        Assert.Equal("admin-1", service.LastActingUserId);
    }

    private static UsersModel CreateModel(StubAdminAuthService service, string actingUserId = "admin-1")
    {
        return new UsersModel(service)
        {
            PageContext = new PageContext
            {
                HttpContext = new DefaultHttpContext
                {
                    User = new ClaimsPrincipal(new ClaimsIdentity(
                    [
                        new Claim(ClaimTypes.NameIdentifier, actingUserId),
                        new Claim(ClaimTypes.Name, "admin"),
                        new Claim(ClaimTypes.Role, "Admin")
                    ], "Cookies"))
                }
            }
        };
    }

    private sealed class StubAdminAuthService : ILocalAuthService
    {
        public IReadOnlyList<LocalUserSummary> Users { get; set; } = [];

        public string? LastCreateUsername { get; private set; }

        public bool LastCreateIsAdmin { get; private set; }

        public string? LastResetUserId { get; private set; }

        public string? LastDeleteUserId { get; private set; }

        public string? LastActingUserId { get; private set; }

        public Task EnsureBootstrapAdminAsync(CancellationToken cancellationToken)
        {
            _ = cancellationToken;
            return Task.CompletedTask;
        }

        public Task<LocalAuthenticationResult> AuthenticateAsync(string username, string password, CancellationToken cancellationToken)
        {
            _ = username;
            _ = password;
            _ = cancellationToken;
            return Task.FromResult(LocalAuthenticationResult.Failed);
        }

        public Task RecordSuccessfulLoginAsync(string userId, CancellationToken cancellationToken)
        {
            _ = userId;
            _ = cancellationToken;
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<LocalUserSummary>> ListUsersAsync(CancellationToken cancellationToken)
        {
            _ = cancellationToken;
            return Task.FromResult(Users);
        }

        public Task<LocalUserRecord?> FindUserByIdAsync(string userId, CancellationToken cancellationToken)
        {
            _ = userId;
            _ = cancellationToken;
            return Task.FromResult<LocalUserRecord?>(null);
        }

        public Task<LocalUserCommandResult> CreateUserAsync(string username, string password, bool isAdmin, CancellationToken cancellationToken)
        {
            _ = password;
            _ = cancellationToken;
            LastCreateUsername = username;
            LastCreateIsAdmin = isAdmin;
            return Task.FromResult(LocalUserCommandResult.Success("created"));
        }

        public Task<LocalUserCommandResult> ResetPasswordAsync(string userId, string newPassword, CancellationToken cancellationToken)
        {
            _ = newPassword;
            _ = cancellationToken;
            LastResetUserId = userId;
            return Task.FromResult(LocalUserCommandResult.Success("reset"));
        }

        public Task<LocalUserCommandResult> SetUserActiveStateAsync(string userId, bool isActive, string actingUserId, CancellationToken cancellationToken)
        {
            _ = userId;
            _ = isActive;
            LastActingUserId = actingUserId;
            _ = cancellationToken;
            return Task.FromResult(LocalUserCommandResult.Success("updated"));
        }

        public Task<LocalUserCommandResult> DeleteUserAsync(string userId, string actingUserId, CancellationToken cancellationToken)
        {
            LastDeleteUserId = userId;
            LastActingUserId = actingUserId;
            _ = cancellationToken;
            return Task.FromResult(LocalUserCommandResult.Success("deleted"));
        }
    }
}

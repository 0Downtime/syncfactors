using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.Options;
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
                new LocalUserSummary("user-1", "alice", "Operator", true, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, null, 0, null)
            ]
        };
        var model = CreateModel(service);

        await model.OnGetAsync(CancellationToken.None);

        Assert.Single(model.Users);
        Assert.Equal("alice", model.Users[0].Username);
    }

    [Fact]
    public async Task OnGetAsync_LoadsOidcGroupsAndKnownMembers()
    {
        var oidcStore = new StubOidcAccountStore
        {
            Accounts =
            [
                new OidcAccountRecord(
                    Subject: "subject-1",
                    Username: "alice@example.com",
                    DisplayName: "Alice Admin",
                    AccessLevel: SecurityRoles.Admin,
                    Groups: ["sync-admins", "sync-viewers"],
                    FirstSeenAt: DateTimeOffset.Parse("2026-04-01T10:00:00Z"),
                    LastLoginAt: DateTimeOffset.Parse("2026-04-02T10:00:00Z")),
                new OidcAccountRecord(
                    Subject: "subject-2",
                    Username: "oliver@example.com",
                    DisplayName: null,
                    AccessLevel: SecurityRoles.Operator,
                    Groups: ["sync-operators"],
                    FirstSeenAt: DateTimeOffset.Parse("2026-04-01T11:00:00Z"),
                    LastLoginAt: DateTimeOffset.Parse("2026-04-02T11:00:00Z"))
            ]
        };
        var model = CreateModel(new StubAdminAuthService(), oidcStore, mode: "oidc", oidcOptions: new OidcOptions
        {
            AdminGroups = ["sync-admins"],
            OperatorGroups = ["sync-operators"],
            ViewerGroups = ["sync-viewers"]
        });

        await model.OnGetAsync(CancellationToken.None);

        Assert.True(model.IsOidcAccessOverviewEnabled);
        Assert.Equal(3, model.OidcGroups.Count);
        var adminGroup = model.OidcGroups.Single(group => group.GroupName == "sync-admins");
        Assert.Equal(SecurityRoles.Admin, adminGroup.AccessLevel);
        var member = Assert.Single(adminGroup.Members);
        Assert.Equal("alice@example.com", member.Username);
        Assert.Equal(SecurityRoles.Admin, member.AccessLevel);
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
        var model = CreateModel(service, actingUserId: "admin-1");

        var result = await model.OnPostDeleteAsync("user-2", CancellationToken.None);

        Assert.IsType<RedirectToPageResult>(result);
        Assert.Equal("user-2", service.LastDeleteUserId);
        Assert.Equal("admin-1", service.LastActingUserId);
    }

    [Fact]
    public async Task OnPostChangeRoleAsync_UsesRequestedRoleAndActor()
    {
        var service = new StubAdminAuthService();
        var model = CreateModel(service, actingUserId: "admin-1");

        var result = await model.OnPostChangeRoleAsync("user-2", true, CancellationToken.None);

        Assert.IsType<RedirectToPageResult>(result);
        Assert.Equal("user-2", service.LastRoleUserId);
        Assert.True(service.LastMakeAdmin);
        Assert.Equal("admin-1", service.LastActingUserId);
    }

    [Fact]
    public async Task OnPostCreateAsync_RejectsChangesWhenLocalUserManagementIsDisabled()
    {
        var service = new StubAdminAuthService
        {
            IsLocalAuthenticationEnabled = false
        };
        var model = CreateModel(service, mode: "oidc");
        model.CreateUsername = "alice";
        model.CreatePassword = "Password1234";
        model.CreatePasswordConfirmation = "Password1234";

        var result = await model.OnPostCreateAsync(CancellationToken.None);

        Assert.IsType<RedirectToPageResult>(result);
        Assert.Equal("Local break-glass user management is disabled while SSO-only mode is active.", model.ErrorMessage);
        Assert.Null(service.LastCreateUsername);
    }

    [Fact]
    public async Task ModeProperties_ReflectConfiguredAuthenticationMode()
    {
        var model = CreateModel(new StubAdminAuthService(), mode: "hybrid");

        Assert.Equal("hybrid", model.AuthenticationMode);
        Assert.Equal("SSO + Break-Glass", model.AuthenticationModeLabel);
        Assert.Equal("good", model.AuthenticationModeBadgeClass);
        Assert.True(model.IsLocalUserManagementEnabled);
    }

    private static UsersModel CreateModel(
        StubAdminAuthService service,
        StubOidcAccountStore? oidcStore = null,
        string actingUserId = "admin-1",
        string mode = "local-break-glass",
        OidcOptions? oidcOptions = null)
    {
        return new UsersModel(
            service,
            oidcStore ?? new StubOidcAccountStore(),
            Options.Create(new LocalAuthOptions
            {
                Mode = mode,
                Oidc = oidcOptions ?? new OidcOptions()
            }))
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

    private sealed class StubOidcAccountStore : IOidcAccountStore
    {
        public IReadOnlyList<OidcAccountRecord> Accounts { get; set; } = [];

        public Task UpsertAsync(OidcAccountRecord account, CancellationToken cancellationToken)
        {
            _ = account;
            _ = cancellationToken;
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<OidcAccountRecord>> ListAccountsAsync(CancellationToken cancellationToken)
        {
            _ = cancellationToken;
            return Task.FromResult(Accounts);
        }
    }

    private sealed class StubAdminAuthService : ILocalAuthService
    {
        public bool IsLocalAuthenticationEnabled { get; set; } = true;

        public IReadOnlyList<LocalUserSummary> Users { get; set; } = [];

        public string? LastCreateUsername { get; private set; }

        public bool LastCreateIsAdmin { get; private set; }

        public string? LastResetUserId { get; private set; }

        public string? LastRoleUserId { get; private set; }

        public bool LastMakeAdmin { get; private set; }

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

        public Task<LocalUserCommandResult> SetUserRoleAsync(string userId, bool isAdmin, string actingUserId, CancellationToken cancellationToken)
        {
            LastRoleUserId = userId;
            LastMakeAdmin = isAdmin;
            LastActingUserId = actingUserId;
            _ = cancellationToken;
            return Task.FromResult(LocalUserCommandResult.Success("role"));
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

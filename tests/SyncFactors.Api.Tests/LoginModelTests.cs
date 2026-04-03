using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using SyncFactors.Api.Pages;
using SyncFactors.Infrastructure;

namespace SyncFactors.Api.Tests;

public sealed class LoginModelTests
{
    [Fact]
    public async Task OnPostAsync_SignsInWithPersistentCookieWhenRememberMeChecked()
    {
        var authService = new StubLocalAuthService(new LocalUserRecord(
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
            LockoutEndAt: null));
        var authenticationService = new CapturingAuthenticationService();
        var model = CreateModel(authService, authenticationService, new LocalAuthOptions
        {
            Mode = "local-break-glass",
            AllowRememberMe = true,
            LocalBreakGlass = new LocalBreakGlassOptions
            {
                Enabled = true
            }
        });
        model.Username = "operator";
        model.Password = "secret";
        model.RememberMe = true;

        var result = await model.OnPostAsync(CancellationToken.None);

        var redirect = Assert.IsType<LocalRedirectResult>(result);
        Assert.Equal("/", redirect.Url);
        Assert.Equal("operator", authenticationService.LastPrincipal?.Identity?.Name);
        Assert.True(authenticationService.LastProperties?.IsPersistent);
        Assert.NotNull(authenticationService.LastProperties?.ExpiresUtc);
    }

    [Fact]
    public async Task OnPostAsync_UsesSessionCookieWhenRememberMeNotChecked()
    {
        var authenticationService = new CapturingAuthenticationService();
        var model = CreateModel(
            new StubLocalAuthService(new LocalUserRecord(
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
                LockoutEndAt: null)),
            authenticationService);
        model.Username = "operator";
        model.Password = "secret";

        await model.OnPostAsync(CancellationToken.None);

        Assert.False(authenticationService.LastProperties?.IsPersistent ?? true);
        Assert.NotNull(authenticationService.LastProperties?.ExpiresUtc);
    }

    [Fact]
    public async Task OnPostAsync_ReturnsPageWhenCredentialsAreInvalid()
    {
        var model = CreateModel(new StubLocalAuthService(null), new CapturingAuthenticationService());
        model.Username = "operator";
        model.Password = "wrong";

        var result = await model.OnPostAsync(CancellationToken.None);

        Assert.IsType<PageResult>(result);
        Assert.Equal("Invalid username or password.", model.ErrorMessage);
    }

    [Fact]
    public void OnGet_RedirectsAuthenticatedUsersToReturnUrl()
    {
        var model = CreateModel(new StubLocalAuthService(null), new CapturingAuthenticationService());
        model.ReturnUrl = "/Sync";
        model.PageContext.HttpContext.User = new ClaimsPrincipal(
            new ClaimsIdentity([new Claim(ClaimTypes.Name, "operator")], CookieAuthenticationDefaults.AuthenticationScheme));

        var result = model.OnGet();

        var redirect = Assert.IsType<LocalRedirectResult>(result);
        Assert.Equal("/Sync", redirect.Url);
    }

    [Fact]
    public async Task Logout_OnPostAsync_ClearsCookieAndRedirectsToLogin()
    {
        var authenticationService = new CapturingAuthenticationService();
        var model = new LogoutModel
        {
            PageContext = new PageContext
            {
                HttpContext = BuildHttpContext(authenticationService)
            }
        };

        var result = await model.OnPostAsync();

        Assert.True(authenticationService.SignOutCalled);
        var redirect = Assert.IsType<RedirectToPageResult>(result);
        Assert.Equal("/Login", redirect.PageName);
    }

    private static LoginModel CreateModel(StubLocalAuthService authService, CapturingAuthenticationService authenticationService, LocalAuthOptions? options = null)
    {
        var model = new LoginModel(authService, Options.Create(options ?? new LocalAuthOptions
        {
            Mode = "local-break-glass",
            LocalBreakGlass = new LocalBreakGlassOptions
            {
                Enabled = true
            }
        }))
        {
            Url = new StubUrlHelper()
        };
        model.PageContext = new PageContext
        {
            HttpContext = BuildHttpContext(authenticationService)
        };
        return model;
    }

    private static DefaultHttpContext BuildHttpContext(CapturingAuthenticationService authenticationService)
    {
        var services = new ServiceCollection()
            .AddSingleton<IAuthenticationService>(authenticationService)
            .BuildServiceProvider();

        return new DefaultHttpContext
        {
            RequestServices = services
        };
    }

    private sealed class StubLocalAuthService(LocalUserRecord? user) : ILocalAuthService
    {
        public bool IsLocalAuthenticationEnabled => true;

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
            return Task.FromResult(user is null ? LocalAuthenticationResult.Failed : new LocalAuthenticationResult(true, user));
        }

        public Task RecordSuccessfulLoginAsync(string userId, CancellationToken cancellationToken)
        {
            _ = cancellationToken;
            _ = userId;
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<LocalUserSummary>> ListUsersAsync(CancellationToken cancellationToken)
        {
            _ = cancellationToken;
            return Task.FromResult<IReadOnlyList<LocalUserSummary>>([]);
        }

        public Task<LocalUserRecord?> FindUserByIdAsync(string userId, CancellationToken cancellationToken)
        {
            _ = userId;
            _ = cancellationToken;
            return Task.FromResult(user);
        }

        public Task<LocalUserCommandResult> CreateUserAsync(string username, string password, bool isAdmin, CancellationToken cancellationToken)
        {
            _ = username;
            _ = password;
            _ = isAdmin;
            _ = cancellationToken;
            return Task.FromResult(LocalUserCommandResult.Success("created"));
        }

        public Task<LocalUserCommandResult> ResetPasswordAsync(string userId, string newPassword, CancellationToken cancellationToken)
        {
            _ = userId;
            _ = newPassword;
            _ = cancellationToken;
            return Task.FromResult(LocalUserCommandResult.Success("reset"));
        }

        public Task<LocalUserCommandResult> SetUserRoleAsync(string userId, bool isAdmin, string actingUserId, CancellationToken cancellationToken)
        {
            _ = userId;
            _ = isAdmin;
            _ = actingUserId;
            _ = cancellationToken;
            return Task.FromResult(LocalUserCommandResult.Success("role"));
        }

        public Task<LocalUserCommandResult> SetUserActiveStateAsync(string userId, bool isActive, string actingUserId, CancellationToken cancellationToken)
        {
            _ = userId;
            _ = isActive;
            _ = actingUserId;
            _ = cancellationToken;
            return Task.FromResult(LocalUserCommandResult.Success("updated"));
        }

        public Task<LocalUserCommandResult> DeleteUserAsync(string userId, string actingUserId, CancellationToken cancellationToken)
        {
            _ = userId;
            _ = actingUserId;
            _ = cancellationToken;
            return Task.FromResult(LocalUserCommandResult.Success("deleted"));
        }
    }

    private sealed class CapturingAuthenticationService : IAuthenticationService
    {
        public ClaimsPrincipal? LastPrincipal { get; private set; }

        public AuthenticationProperties? LastProperties { get; private set; }

        public bool SignOutCalled { get; private set; }

        public Task<AuthenticateResult> AuthenticateAsync(HttpContext context, string? scheme) =>
            Task.FromResult(AuthenticateResult.NoResult());

        public Task ChallengeAsync(HttpContext context, string? scheme, AuthenticationProperties? properties) =>
            Task.CompletedTask;

        public Task ForbidAsync(HttpContext context, string? scheme, AuthenticationProperties? properties) =>
            Task.CompletedTask;

        public Task SignInAsync(HttpContext context, string? scheme, ClaimsPrincipal principal, AuthenticationProperties? properties)
        {
            LastPrincipal = principal;
            LastProperties = properties;
            return Task.CompletedTask;
        }

        public Task SignOutAsync(HttpContext context, string? scheme, AuthenticationProperties? properties)
        {
            SignOutCalled = true;
            return Task.CompletedTask;
        }
    }

    private sealed class StubUrlHelper : Microsoft.AspNetCore.Mvc.IUrlHelper
    {
        public ActionContext ActionContext => new();

        public string? Action(Microsoft.AspNetCore.Mvc.Routing.UrlActionContext actionContext) => null;

        public string? Content(string? contentPath) => contentPath;

        public bool IsLocalUrl(string? url) => !string.IsNullOrWhiteSpace(url) && url.StartsWith("/", StringComparison.Ordinal);

        public string? Link(string? routeName, object? values) => null;

        public string? RouteUrl(Microsoft.AspNetCore.Mvc.Routing.UrlRouteContext routeContext) => null;

        public string? Page(string? pageName, string? pageHandler, object? values, string? protocol, string? host, string? fragment) =>
            pageName == "/Index" ? "/" : pageName;
    }
}

using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using SyncFactors.Infrastructure;

namespace SyncFactors.Api;

public sealed record SessionResponse(
    bool IsAuthenticated,
    string? UserId,
    string? Username,
    string? Role,
    bool IsAdmin);

public sealed record SessionLoginRequest(
    string Username,
    string Password,
    bool RememberMe,
    string? ReturnUrl);

public sealed record CreatePreviewRequest(
    string WorkerId);

public sealed record DeleteAllUsersRequest(
    string ConfirmationText);

public sealed record CreateLocalUserRequest(
    string Username,
    string Password,
    bool IsAdmin);

public sealed record ResetLocalUserPasswordRequest(
    string NewPassword);

public sealed record SetLocalUserRoleRequest(
    bool IsAdmin);

public sealed record SetLocalUserActiveStateRequest(
    bool IsActive);

public static class LocalSessionManager
{
    public static async Task<SessionResponse> BuildSessionResponseAsync(
        HttpContext httpContext,
        ILocalAuthService authService,
        CancellationToken cancellationToken)
    {
        if (httpContext.User.Identity?.IsAuthenticated != true)
        {
            return AnonymousSession;
        }

        var userId = httpContext.User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrWhiteSpace(userId))
        {
            await SignOutAsync(httpContext);
            return AnonymousSession;
        }

        var authSource = httpContext.User.FindFirstValue(SecurityClaimTypes.AuthSource);
        if (!string.Equals(authSource, "local", StringComparison.Ordinal))
        {
            var username = httpContext.User.Identity?.Name ?? httpContext.User.FindFirstValue(ClaimTypes.Name);
            var role = httpContext.User.FindFirstValue(ClaimTypes.Role);
            return new SessionResponse(
                IsAuthenticated: true,
                UserId: userId,
                Username: username,
                Role: role,
                IsAdmin: IsAdminRole(role));
        }

        var currentUser = await authService.FindUserByIdAsync(userId, cancellationToken);
        if (currentUser is null || !currentUser.IsActive)
        {
            await SignOutAsync(httpContext);
            return AnonymousSession;
        }

        return new SessionResponse(
            IsAuthenticated: true,
            UserId: currentUser.UserId,
            Username: currentUser.Username,
            Role: currentUser.Role,
            IsAdmin: IsAdminRole(currentUser.Role));
    }

    public static async Task SignInAsync(HttpContext httpContext, LocalUserRecord user, bool rememberMe, LocalAuthOptions authOptions)
    {
        var persistent = authOptions.AllowRememberMe && rememberMe;
        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, user.UserId),
            new(ClaimTypes.Name, user.Username),
            new(ClaimTypes.Role, user.Role),
            new(SecurityClaimTypes.AuthSource, "local"),
            new(SecurityClaimTypes.SessionIssuedAt, DateTimeOffset.UtcNow.ToString("O"))
        };

        var identity = new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme);

        await httpContext.SignInAsync(
            CookieAuthenticationDefaults.AuthenticationScheme,
            new ClaimsPrincipal(identity),
            new AuthenticationProperties
            {
                IsPersistent = persistent,
                ExpiresUtc = DateTimeOffset.UtcNow.Add(
                    persistent
                        ? authOptions.GetRememberMeSessionLifetime()
                        : authOptions.GetAbsoluteSessionLifetime())
            });
    }

    public static Task SignOutAsync(HttpContext httpContext) =>
        httpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);

    public static SessionResponse AnonymousSession { get; } =
        new(
            IsAuthenticated: false,
            UserId: null,
            Username: null,
            Role: null,
            IsAdmin: false);

    private static bool IsAdminRole(string? role) =>
        string.Equals(role, SecurityRoles.Admin, StringComparison.OrdinalIgnoreCase) ||
        string.Equals(role, SecurityRoles.BreakGlassAdmin, StringComparison.OrdinalIgnoreCase);
}

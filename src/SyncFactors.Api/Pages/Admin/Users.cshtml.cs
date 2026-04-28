using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.Options;
using SyncFactors.Infrastructure;

namespace SyncFactors.Api.Pages.Admin;

[Authorize(Roles = "Admin,BreakGlassAdmin")]
public sealed class UsersModel(
    ILocalAuthService localAuthService,
    IOidcAccountStore oidcAccountStore,
    IOptions<LocalAuthOptions> authOptions) : PageModel
{
    [BindProperty]
    public string CreateUsername { get; set; } = string.Empty;

    [BindProperty]
    public string CreatePassword { get; set; } = string.Empty;

    [BindProperty]
    public string CreatePasswordConfirmation { get; set; } = string.Empty;

    [BindProperty]
    public bool CreateIsAdmin { get; set; }

    public IReadOnlyList<LocalUserSummary> Users { get; private set; } = [];

    public IReadOnlyList<OidcGroupAccessSummary> OidcGroups { get; private set; } = [];

    [TempData]
    public string? ErrorMessage { get; set; }

    [TempData]
    public string? SuccessMessage { get; set; }

    public bool IsLocalUserManagementEnabled => localAuthService.IsLocalAuthenticationEnabled;

    public string AuthenticationMode => authOptions.Value.Mode?.Trim().ToLowerInvariant() switch
    {
        "oidc" => "oidc",
        "hybrid" => "hybrid",
        _ => "local-break-glass"
    };

    public string AuthenticationModeLabel => AuthenticationMode switch
    {
        "oidc" => "SSO Only",
        "hybrid" => "SSO + Break-Glass",
        _ => "Local Break-Glass Only"
    };

    public string AuthenticationModeBadgeClass => AuthenticationMode switch
    {
        "oidc" => "info",
        "hybrid" => "good",
        _ => "warn"
    };

    public string AuthenticationModeDescription => AuthenticationMode switch
    {
        "oidc" => "Enterprise SSO is required. Local break-glass accounts are disabled on this deployment.",
        "hybrid" => "Enterprise SSO is the primary sign-in path, and local break-glass accounts remain available for emergency access.",
        _ => "This deployment is using local break-glass authentication without enterprise SSO."
    };

    public string AccountManagementDescription => AuthenticationMode switch
    {
        "oidc" => "Manage sign-in and role assignments in your identity provider. Local usernames and passwords are not available here.",
        "hybrid" => "Use this page to manage local break-glass accounts. Regular operator access should continue through enterprise SSO.",
        _ => "Use this page to manage the local accounts that sign in directly to the portal."
    };

    public bool IsOidcAccessOverviewEnabled =>
        (string.Equals(AuthenticationMode, "oidc", StringComparison.Ordinal) ||
         string.Equals(AuthenticationMode, "hybrid", StringComparison.Ordinal)) &&
        OidcRoleResolver.HasConfiguredRoleGroups(authOptions.Value);

    public string AccessBadgeClass(string accessLevel) => accessLevel switch
    {
        SecurityRoles.Admin => "info",
        SecurityRoles.Operator => "good",
        SecurityRoles.Viewer => "neutral",
        _ => "dim"
    };

    public async Task OnGetAsync(CancellationToken cancellationToken)
    {
        Users = await localAuthService.ListUsersAsync(cancellationToken);
        if (IsOidcAccessOverviewEnabled)
        {
            var accounts = await oidcAccountStore.ListAccountsAsync(cancellationToken);
            OidcGroups = BuildOidcGroups(authOptions.Value, accounts);
        }
    }

    public async Task<IActionResult> OnPostCreateAsync(CancellationToken cancellationToken)
    {
        if (!EnsureLocalUserManagementAvailable())
        {
            return RedirectToPage();
        }

        if (!string.Equals(CreatePassword, CreatePasswordConfirmation, StringComparison.Ordinal))
        {
            ErrorMessage = "Create password and confirmation must match.";
            SuccessMessage = null;
            return RedirectToPage();
        }

        var result = await localAuthService.CreateUserAsync(CreateUsername, CreatePassword, CreateIsAdmin, cancellationToken);
        SetFlash(result);
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostResetPasswordAsync(string userId, string newPassword, string confirmPassword, CancellationToken cancellationToken)
    {
        if (!EnsureLocalUserManagementAvailable())
        {
            return RedirectToPage();
        }

        if (!string.Equals(newPassword, confirmPassword, StringComparison.Ordinal))
        {
            ErrorMessage = "Reset password and confirmation must match.";
            SuccessMessage = null;
            return RedirectToPage();
        }

        var result = await localAuthService.ResetPasswordAsync(userId, newPassword, cancellationToken);
        SetFlash(result);
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostChangeRoleAsync(string userId, bool makeAdmin, CancellationToken cancellationToken)
    {
        if (!EnsureLocalUserManagementAvailable())
        {
            return RedirectToPage();
        }

        var result = await localAuthService.SetUserRoleAsync(userId, makeAdmin, GetActingUserId(), cancellationToken);
        SetFlash(result);
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostToggleActiveAsync(string userId, bool makeActive, CancellationToken cancellationToken)
    {
        if (!EnsureLocalUserManagementAvailable())
        {
            return RedirectToPage();
        }

        var result = await localAuthService.SetUserActiveStateAsync(userId, makeActive, GetActingUserId(), cancellationToken);
        SetFlash(result);
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostDeleteAsync(string userId, CancellationToken cancellationToken)
    {
        if (!EnsureLocalUserManagementAvailable())
        {
            return RedirectToPage();
        }

        var result = await localAuthService.DeleteUserAsync(userId, GetActingUserId(), cancellationToken);
        SetFlash(result);
        return RedirectToPage();
    }

    private string GetActingUserId() =>
        User.FindFirstValue(ClaimTypes.NameIdentifier) ?? string.Empty;

    private void SetFlash(LocalUserCommandResult result)
    {
        if (result.Succeeded)
        {
            SuccessMessage = result.Message;
            ErrorMessage = null;
            return;
        }

        ErrorMessage = result.Message;
        SuccessMessage = null;
    }

    private bool EnsureLocalUserManagementAvailable()
    {
        if (IsLocalUserManagementEnabled)
        {
            return true;
        }

        ErrorMessage = "Local break-glass user management is disabled while SSO-only mode is active.";
        SuccessMessage = null;
        return false;
    }

    private static IReadOnlyList<OidcGroupAccessSummary> BuildOidcGroups(
        LocalAuthOptions authOptions,
        IReadOnlyList<OidcAccountRecord> accounts)
    {
        var configuredGroups = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        AddGroups(configuredGroups, authOptions.Oidc.AdminGroups, SecurityRoles.Admin);
        AddGroups(configuredGroups, authOptions.Oidc.OperatorGroups, SecurityRoles.Operator);
        AddGroups(configuredGroups, authOptions.Oidc.ViewerGroups, SecurityRoles.Viewer);

        return configuredGroups
            .OrderByDescending(pair => AccessPriority(pair.Value))
            .ThenBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase)
            .Select(pair =>
            {
                var members = accounts
                    .Where(account => account.Groups.Any(group => string.Equals(group, pair.Key, StringComparison.OrdinalIgnoreCase)))
                    .OrderBy(account => account.Username, StringComparer.OrdinalIgnoreCase)
                    .ThenBy(account => account.Subject, StringComparer.OrdinalIgnoreCase)
                    .Select(account => new OidcGroupMemberSummary(
                        Subject: account.Subject,
                        Username: account.Username,
                        DisplayName: account.DisplayName,
                        AccessLevel: account.AccessLevel,
                        LastLoginAt: account.LastLoginAt))
                    .ToArray();

                return new OidcGroupAccessSummary(pair.Key, pair.Value, members);
            })
            .ToArray();
    }

    private static void AddGroups(Dictionary<string, string> configuredGroups, IEnumerable<string> groups, string accessLevel)
    {
        foreach (var group in groups)
        {
            var trimmed = group.Trim();
            if (string.IsNullOrWhiteSpace(trimmed))
            {
                continue;
            }

            if (!configuredGroups.TryGetValue(trimmed, out var existingAccess) ||
                AccessPriority(accessLevel) > AccessPriority(existingAccess))
            {
                configuredGroups[trimmed] = accessLevel;
            }
        }
    }

    private static int AccessPriority(string accessLevel) => accessLevel switch
    {
        SecurityRoles.Admin => 3,
        SecurityRoles.Operator => 2,
        SecurityRoles.Viewer => 1,
        _ => 0
    };

    public sealed record OidcGroupAccessSummary(
        string GroupName,
        string AccessLevel,
        IReadOnlyList<OidcGroupMemberSummary> Members);

    public sealed record OidcGroupMemberSummary(
        string Subject,
        string Username,
        string? DisplayName,
        string AccessLevel,
        DateTimeOffset LastLoginAt);
}

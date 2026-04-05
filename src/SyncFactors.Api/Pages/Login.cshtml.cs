using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.Options;
using SyncFactors.Infrastructure;

namespace SyncFactors.Api.Pages;

[AllowAnonymous]
public sealed class LoginModel(ILocalAuthService localAuthService, IOptions<LocalAuthOptions> authOptions) : PageModel
{
    [BindProperty]
    public string Username { get; set; } = string.Empty;

    [BindProperty]
    public string Password { get; set; } = string.Empty;

    [BindProperty]
    public bool RememberMe { get; set; }

    [BindProperty(SupportsGet = true)]
    public string? ReturnUrl { get; set; }

    [BindProperty(SupportsGet = true)]
    public bool LoggedOut { get; set; }

    public bool IsLocalLoginEnabled => localAuthService.IsLocalAuthenticationEnabled;

    public bool IsOidcEnabled =>
        string.Equals(authOptions.Value.Mode, "oidc", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(authOptions.Value.Mode, "hybrid", StringComparison.OrdinalIgnoreCase);

    public bool CanRememberMe => authOptions.Value.AllowRememberMe;

    public string? ErrorMessage { get; private set; }

    public IActionResult OnGet()
    {
        if (User.Identity?.IsAuthenticated == true)
        {
            return LocalRedirect(ResolveReturnUrl());
        }

        if (!LoggedOut && !IsLocalLoginEnabled && IsOidcEnabled)
        {
            return BuildSsoChallenge();
        }

        return Page();
    }

    public async Task<IActionResult> OnPostAsync(CancellationToken cancellationToken)
    {
        if (!IsLocalLoginEnabled)
        {
            return IsOidcEnabled ? BuildSsoChallenge() : Page();
        }

        var result = await localAuthService.AuthenticateAsync(Username, Password, cancellationToken);
        if (!result.Succeeded || result.User is null)
        {
            ErrorMessage = result.FailureMessage ?? "Invalid username or password.";
            Password = string.Empty;
            return Page();
        }

        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, result.User.UserId),
            new(ClaimTypes.Name, result.User.Username),
            new(ClaimTypes.Role, result.User.Role),
            new(SecurityClaimTypes.AuthSource, "local"),
            new(SecurityClaimTypes.SessionIssuedAt, DateTimeOffset.UtcNow.ToString("O"))
        };
        var identity = new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme);
        var rememberMe = CanRememberMe && RememberMe;

        await HttpContext.SignInAsync(
            CookieAuthenticationDefaults.AuthenticationScheme,
            new ClaimsPrincipal(identity),
            new AuthenticationProperties
            {
                IsPersistent = rememberMe,
                ExpiresUtc = DateTimeOffset.UtcNow.AddHours(rememberMe
                    ? Math.Max(1, authOptions.Value.RememberMeSessionHours)
                    : Math.Clamp(authOptions.Value.AbsoluteSessionHours, 8, 12))
            });
        await localAuthService.RecordSuccessfulLoginAsync(result.User.UserId, cancellationToken);
        return LocalRedirect(ResolveReturnUrl());
    }

    public IActionResult OnPostSso()
    {
        return BuildSsoChallenge();
    }

    private string ResolveReturnUrl() =>
        string.IsNullOrWhiteSpace(ReturnUrl) || !Url.IsLocalUrl(ReturnUrl)
            ? "/"
            : ReturnUrl;

    private IActionResult BuildSsoChallenge() =>
        Challenge(
            new AuthenticationProperties
            {
                RedirectUri = ResolveReturnUrl()
            },
            OpenIdConnectDefaults.AuthenticationScheme);
}

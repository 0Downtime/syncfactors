using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using SyncFactors.Infrastructure;

namespace SyncFactors.Api.Pages;

[AllowAnonymous]
public sealed class LoginModel(ILocalAuthService localAuthService) : PageModel
{
    private static readonly TimeSpan RememberMeDuration = TimeSpan.FromDays(30);

    [BindProperty]
    public string Username { get; set; } = string.Empty;

    [BindProperty]
    public string Password { get; set; } = string.Empty;

    [BindProperty]
    public bool RememberMe { get; set; }

    [BindProperty(SupportsGet = true)]
    public string? ReturnUrl { get; set; }

    public string? ErrorMessage { get; private set; }

    public IActionResult OnGet()
    {
        if (User.Identity?.IsAuthenticated == true)
        {
            return LocalRedirect(ResolveReturnUrl());
        }

        return Page();
    }

    public async Task<IActionResult> OnPostAsync(CancellationToken cancellationToken)
    {
        var result = await localAuthService.AuthenticateAsync(Username, Password, cancellationToken);
        if (!result.Succeeded || result.User is null)
        {
            ErrorMessage = "Invalid username or password.";
            Password = string.Empty;
            return Page();
        }

        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, result.User.UserId),
            new(ClaimTypes.Name, result.User.Username),
            new(ClaimTypes.Role, result.User.Role)
        };
        var identity = new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme);

        await HttpContext.SignInAsync(
            CookieAuthenticationDefaults.AuthenticationScheme,
            new ClaimsPrincipal(identity),
            new AuthenticationProperties
            {
                IsPersistent = RememberMe,
                ExpiresUtc = RememberMe ? DateTimeOffset.UtcNow.Add(RememberMeDuration) : null
            });

        await localAuthService.RecordSuccessfulLoginAsync(result.User.UserId, cancellationToken);
        return LocalRedirect(ResolveReturnUrl());
    }

    private string ResolveReturnUrl() =>
        string.IsNullOrWhiteSpace(ReturnUrl) || !Url.IsLocalUrl(ReturnUrl)
            ? "/"
            : ReturnUrl;
}

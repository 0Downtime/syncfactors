using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using SyncFactors.Infrastructure;

namespace SyncFactors.Api.Pages;

public sealed class LogoutModel : PageModel
{
    public async Task<IActionResult> OnPostAsync()
    {
        var authSource = User.FindFirst(SecurityClaimTypes.AuthSource)?.Value;
        await HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
        if (string.Equals(authSource, "oidc", StringComparison.Ordinal))
        {
            return SignOut(
                new AuthenticationProperties
                {
                    RedirectUri = Url.Page("/Login")
                },
                CookieAuthenticationDefaults.AuthenticationScheme,
                OpenIdConnectDefaults.AuthenticationScheme);
        }

        return RedirectToPage("/Login");
    }
}

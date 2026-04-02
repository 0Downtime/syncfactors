using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using SyncFactors.Infrastructure;

namespace SyncFactors.Api.Pages.Admin;

[Authorize(Roles = "Admin")]
public sealed class UsersModel(ILocalAuthService localAuthService) : PageModel
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

    [TempData]
    public string? ErrorMessage { get; set; }

    [TempData]
    public string? SuccessMessage { get; set; }

    public async Task OnGetAsync(CancellationToken cancellationToken)
    {
        Users = await localAuthService.ListUsersAsync(cancellationToken);
    }

    public async Task<IActionResult> OnPostCreateAsync(CancellationToken cancellationToken)
    {
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

    public async Task<IActionResult> OnPostToggleActiveAsync(string userId, bool makeActive, CancellationToken cancellationToken)
    {
        var result = await localAuthService.SetUserActiveStateAsync(userId, makeActive, GetActingUserId(), cancellationToken);
        SetFlash(result);
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostDeleteAsync(string userId, CancellationToken cancellationToken)
    {
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
}

using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;

namespace SyncFactors.Infrastructure;

public sealed class LocalAuthService(
    ILocalUserStore userStore,
    IPasswordHasher<LocalUserRecord> passwordHasher,
    IOptions<LocalAuthOptions> options,
    TimeProvider timeProvider) : ILocalAuthService
{
    private const int MinimumPasswordLength = 12;

    public async Task EnsureBootstrapAdminAsync(CancellationToken cancellationToken)
    {
        var bootstrap = options.Value.BootstrapAdmin;
        var hasUsers = await userStore.AnyUsersAsync(cancellationToken);

        if (string.IsNullOrWhiteSpace(bootstrap.Username) || string.IsNullOrWhiteSpace(bootstrap.Password))
        {
            if (!hasUsers)
            {
                throw new InvalidOperationException(
                    "Local authentication requires SyncFactors:Auth:BootstrapAdmin:Username and SyncFactors:Auth:BootstrapAdmin:Password when no local users exist.");
            }

            return;
        }

        var existingUser = await userStore.FindByUsernameAsync(bootstrap.Username, cancellationToken);
        if (existingUser is not null)
        {
            return;
        }

        var now = timeProvider.GetUtcNow();
        var user = new LocalUserRecord(
            UserId: $"local-{Guid.NewGuid():N}",
            Username: bootstrap.Username.Trim(),
            NormalizedUsername: SqliteLocalUserStore.NormalizeUsername(bootstrap.Username),
            PasswordHash: string.Empty,
            Role: "Admin",
            IsActive: true,
            CreatedAt: now,
            UpdatedAt: now,
            LastLoginAt: null);
        var passwordHash = passwordHasher.HashPassword(user, bootstrap.Password);
        await userStore.CreateAsync(user with { PasswordHash = passwordHash }, cancellationToken);
    }

    public async Task<LocalAuthenticationResult> AuthenticateAsync(string username, string password, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(username) || string.IsNullOrWhiteSpace(password))
        {
            return LocalAuthenticationResult.Failed;
        }

        var user = await userStore.FindByUsernameAsync(username, cancellationToken);
        if (user is null || !user.IsActive)
        {
            return LocalAuthenticationResult.Failed;
        }

        var verification = passwordHasher.VerifyHashedPassword(user, user.PasswordHash, password);
        if (verification == PasswordVerificationResult.Failed)
        {
            return LocalAuthenticationResult.Failed;
        }

        if (verification == PasswordVerificationResult.SuccessRehashNeeded)
        {
            var now = timeProvider.GetUtcNow();
            var updatedUser = user with
            {
                PasswordHash = passwordHasher.HashPassword(user, password),
                UpdatedAt = now
            };
            await userStore.UpdateAsync(updatedUser, cancellationToken);
            user = updatedUser;
        }

        return new LocalAuthenticationResult(true, user);
    }

    public Task RecordSuccessfulLoginAsync(string userId, CancellationToken cancellationToken) =>
        userStore.UpdateLastLoginAsync(userId, timeProvider.GetUtcNow(), cancellationToken);

    public Task<IReadOnlyList<LocalUserSummary>> ListUsersAsync(CancellationToken cancellationToken) =>
        userStore.ListUsersAsync(cancellationToken);

    public Task<LocalUserRecord?> FindUserByIdAsync(string userId, CancellationToken cancellationToken) =>
        userStore.FindByIdAsync(userId, cancellationToken);

    public async Task<LocalUserCommandResult> CreateUserAsync(string username, string password, bool isAdmin, CancellationToken cancellationToken)
    {
        var normalizedUsername = NormalizeUsername(username);
        if (normalizedUsername is null)
        {
            return LocalUserCommandResult.Failure("Username is required.");
        }

        var passwordValidation = ValidatePassword(password);
        if (passwordValidation is not null)
        {
            return LocalUserCommandResult.Failure(passwordValidation);
        }

        var existingUser = await userStore.FindByUsernameAsync(normalizedUsername, cancellationToken);
        if (existingUser is not null)
        {
            return LocalUserCommandResult.Failure("A user with that username already exists.");
        }

        var now = timeProvider.GetUtcNow();
        var role = isAdmin ? "Admin" : "Operator";
        var user = new LocalUserRecord(
            UserId: $"local-{Guid.NewGuid():N}",
            Username: username.Trim(),
            NormalizedUsername: normalizedUsername,
            PasswordHash: string.Empty,
            Role: role,
            IsActive: true,
            CreatedAt: now,
            UpdatedAt: now,
            LastLoginAt: null);
        await userStore.CreateAsync(user with { PasswordHash = passwordHasher.HashPassword(user, password) }, cancellationToken);
        return LocalUserCommandResult.Success($"{role} user '{user.Username}' created.");
    }

    public async Task<LocalUserCommandResult> ResetPasswordAsync(string userId, string newPassword, CancellationToken cancellationToken)
    {
        var passwordValidation = ValidatePassword(newPassword);
        if (passwordValidation is not null)
        {
            return LocalUserCommandResult.Failure(passwordValidation);
        }

        var user = await userStore.FindByIdAsync(userId, cancellationToken);
        if (user is null)
        {
            return LocalUserCommandResult.Failure("User could not be found.");
        }

        var now = timeProvider.GetUtcNow();
        var updatedUser = user with
        {
            PasswordHash = passwordHasher.HashPassword(user, newPassword),
            UpdatedAt = now
        };
        await userStore.UpdateAsync(updatedUser, cancellationToken);
        return LocalUserCommandResult.Success($"Password reset for '{user.Username}'.");
    }

    public async Task<LocalUserCommandResult> SetUserActiveStateAsync(string userId, bool isActive, string actingUserId, CancellationToken cancellationToken)
    {
        if (string.Equals(userId, actingUserId, StringComparison.Ordinal))
        {
            return LocalUserCommandResult.Failure("You cannot change the active state of your own account.");
        }

        var user = await userStore.FindByIdAsync(userId, cancellationToken);
        if (user is null)
        {
            return LocalUserCommandResult.Failure("User could not be found.");
        }

        if (user.IsActive == isActive)
        {
            return LocalUserCommandResult.Success($"User '{user.Username}' is already {(isActive ? "active" : "inactive")}.");
        }

        if (!isActive)
        {
            var adminProtection = await ValidateAdminProtectionAsync(user, cancellationToken);
            if (adminProtection is not null)
            {
                return adminProtection;
            }
        }

        var now = timeProvider.GetUtcNow();
        await userStore.UpdateAsync(user with
        {
            IsActive = isActive,
            UpdatedAt = now
        }, cancellationToken);

        return LocalUserCommandResult.Success($"User '{user.Username}' {(isActive ? "reactivated" : "deactivated")}.");
    }

    public async Task<LocalUserCommandResult> DeleteUserAsync(string userId, string actingUserId, CancellationToken cancellationToken)
    {
        if (string.Equals(userId, actingUserId, StringComparison.Ordinal))
        {
            return LocalUserCommandResult.Failure("You cannot delete your own account.");
        }

        var user = await userStore.FindByIdAsync(userId, cancellationToken);
        if (user is null)
        {
            return LocalUserCommandResult.Failure("User could not be found.");
        }

        var adminProtection = await ValidateAdminProtectionAsync(user, cancellationToken);
        if (adminProtection is not null)
        {
            return adminProtection;
        }

        await userStore.DeleteAsync(userId, cancellationToken);
        return LocalUserCommandResult.Success($"User '{user.Username}' deleted.");
    }

    private async Task<LocalUserCommandResult?> ValidateAdminProtectionAsync(LocalUserRecord user, CancellationToken cancellationToken)
    {
        if (!user.IsActive || !string.Equals(user.Role, "Admin", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        return await userStore.CountActiveAdminsAsync(cancellationToken) <= 1
            ? LocalUserCommandResult.Failure("At least one active admin account must remain.")
            : null;
    }

    private static string? NormalizeUsername(string username)
    {
        if (string.IsNullOrWhiteSpace(username))
        {
            return null;
        }

        return SqliteLocalUserStore.NormalizeUsername(username);
    }

    private static string? ValidatePassword(string password)
    {
        if (string.IsNullOrWhiteSpace(password))
        {
            return "Password is required.";
        }

        if (password.Length < MinimumPasswordLength)
        {
            return $"Password must be at least {MinimumPasswordLength} characters.";
        }

        if (!password.Any(char.IsUpper) || !password.Any(char.IsLower) || !password.Any(char.IsDigit))
        {
            return "Password must include uppercase, lowercase, and numeric characters.";
        }

        return null;
    }
}

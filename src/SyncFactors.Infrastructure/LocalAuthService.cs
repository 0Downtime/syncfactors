using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;

namespace SyncFactors.Infrastructure;

public sealed class LocalAuthService(
    ILocalUserStore userStore,
    IPasswordHasher<LocalUserRecord> passwordHasher,
    IOptions<LocalAuthOptions> options,
    TimeProvider timeProvider,
    ISecurityAuditService securityAuditService) : ILocalAuthService
{
    private const int MinimumPasswordLength = 12;

    public bool IsLocalAuthenticationEnabled =>
        string.Equals(options.Value.Mode, "local-break-glass", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(options.Value.Mode, "hybrid", StringComparison.OrdinalIgnoreCase) ||
        options.Value.LocalBreakGlass.Enabled;

    public async Task EnsureBootstrapAdminAsync(CancellationToken cancellationToken)
    {
        if (!IsLocalAuthenticationEnabled)
        {
            return;
        }

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
            Role: SecurityRoles.BreakGlassAdmin,
            IsActive: true,
            CreatedAt: now,
            UpdatedAt: now,
            LastLoginAt: null,
            FailedLoginCount: 0,
            LockoutEndAt: null);
        var passwordHash = passwordHasher.HashPassword(user, bootstrap.Password);
        await userStore.CreateAsync(user with { PasswordHash = passwordHash }, cancellationToken);
        securityAuditService.Write(
            "BootstrapAdminProvisioned",
            "Success",
            ("Username", user.Username),
            ("Role", user.Role));
    }

    public async Task<LocalAuthenticationResult> AuthenticateAsync(string username, string password, CancellationToken cancellationToken)
    {
        if (!IsLocalAuthenticationEnabled)
        {
            return new LocalAuthenticationResult(false, null, "Local sign-in is disabled.");
        }

        if (string.IsNullOrWhiteSpace(username) || string.IsNullOrWhiteSpace(password))
        {
            return new LocalAuthenticationResult(false, null, "Username and password are required.");
        }

        var user = await userStore.FindByUsernameAsync(username, cancellationToken);
        if (user is null || !user.IsActive)
        {
            securityAuditService.Write(
                "LocalAuthenticationFailed",
                "UnknownUser",
                ("Username", username.Trim()));
            return LocalAuthenticationResult.Failed;
        }

        var now = timeProvider.GetUtcNow();
        if (user.LockoutEndAt is not null && user.LockoutEndAt > now)
        {
            securityAuditService.Write(
                "LocalAuthenticationBlocked",
                "LockedOut",
                ("Username", user.Username),
                ("LockoutEndAt", user.LockoutEndAt));
            return new LocalAuthenticationResult(false, null, $"This account is locked until {user.LockoutEndAt:O}.");
        }

        var verification = passwordHasher.VerifyHashedPassword(user, user.PasswordHash, password);
        if (verification == PasswordVerificationResult.Failed)
        {
            var updatedUser = ApplyFailedLogin(user, now);
            await userStore.UpdateAsync(updatedUser, cancellationToken);
            securityAuditService.Write(
                "LocalAuthenticationFailed",
                updatedUser.LockoutEndAt is null ? "BadPassword" : "LockedOut",
                ("UserId", updatedUser.UserId),
                ("Username", updatedUser.Username),
                ("FailedLoginCount", updatedUser.FailedLoginCount),
                ("LockoutEndAt", updatedUser.LockoutEndAt));
            return updatedUser.LockoutEndAt is null
                ? LocalAuthenticationResult.Failed
                : new LocalAuthenticationResult(false, null, $"This account is locked until {updatedUser.LockoutEndAt:O}.");
        }

        var authenticatedUser = user with
        {
            LastLoginAt = now,
            UpdatedAt = now,
            FailedLoginCount = 0,
            LockoutEndAt = null
        };

        if (verification == PasswordVerificationResult.SuccessRehashNeeded)
        {
            authenticatedUser = authenticatedUser with
            {
                PasswordHash = passwordHasher.HashPassword(authenticatedUser, password)
            };
        }

        await userStore.UpdateAsync(authenticatedUser, cancellationToken);
        securityAuditService.Write(
            "LocalAuthenticationSucceeded",
            "Success",
            ("UserId", authenticatedUser.UserId),
            ("Username", authenticatedUser.Username),
            ("Role", authenticatedUser.Role));
        return new LocalAuthenticationResult(true, authenticatedUser);
    }

    public Task RecordSuccessfulLoginAsync(string userId, CancellationToken cancellationToken) =>
        userStore.UpdateLastLoginAsync(userId, timeProvider.GetUtcNow(), cancellationToken);

    public Task<IReadOnlyList<LocalUserSummary>> ListUsersAsync(CancellationToken cancellationToken) =>
        userStore.ListUsersAsync(cancellationToken);

    public Task<LocalUserRecord?> FindUserByIdAsync(string userId, CancellationToken cancellationToken) =>
        userStore.FindByIdAsync(userId, cancellationToken);

    public async Task<LocalUserCommandResult> CreateUserAsync(string username, string password, string role, CancellationToken cancellationToken)
    {
        var normalizedUsername = NormalizeUsername(username);
        if (normalizedUsername is null)
        {
            return LocalUserCommandResult.Failure("Username is required.");
        }

        var normalizedRole = NormalizeManagedRole(role);
        if (normalizedRole is null)
        {
            return LocalUserCommandResult.Failure("Role must be Viewer, Operator, or Admin.");
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
        var user = new LocalUserRecord(
            UserId: $"local-{Guid.NewGuid():N}",
            Username: username.Trim(),
            NormalizedUsername: normalizedUsername,
            PasswordHash: string.Empty,
            Role: normalizedRole,
            IsActive: true,
            CreatedAt: now,
            UpdatedAt: now,
            LastLoginAt: null,
            FailedLoginCount: 0,
            LockoutEndAt: null);
        await userStore.CreateAsync(user with { PasswordHash = passwordHasher.HashPassword(user, password) }, cancellationToken);
        securityAuditService.Write("LocalUserCreated", "Success", ("UserId", user.UserId), ("Username", user.Username), ("Role", user.Role));
        return LocalUserCommandResult.Success($"{normalizedRole} user '{user.Username}' created.");
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
            UpdatedAt = now,
            FailedLoginCount = 0,
            LockoutEndAt = null
        };
        await userStore.UpdateAsync(updatedUser, cancellationToken);
        securityAuditService.Write("LocalPasswordReset", "Success", ("UserId", user.UserId), ("Username", user.Username));
        return LocalUserCommandResult.Success($"Password reset for '{user.Username}'.");
    }

    public async Task<LocalUserCommandResult> SetUserRoleAsync(string userId, string role, string actingUserId, CancellationToken cancellationToken)
    {
        if (string.Equals(userId, actingUserId, StringComparison.Ordinal))
        {
            return LocalUserCommandResult.Failure("You cannot change your own role.");
        }

        var targetRole = NormalizeManagedRole(role);
        if (targetRole is null)
        {
            return LocalUserCommandResult.Failure("Role must be Viewer, Operator, or Admin.");
        }

        var user = await userStore.FindByIdAsync(userId, cancellationToken);
        if (user is null)
        {
            return LocalUserCommandResult.Failure("User could not be found.");
        }

        if (string.Equals(user.Role, targetRole, StringComparison.OrdinalIgnoreCase))
        {
            return LocalUserCommandResult.Success($"User '{user.Username}' already has role {targetRole}.");
        }

        if (!string.Equals(targetRole, SecurityRoles.Admin, StringComparison.OrdinalIgnoreCase))
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
            Role = targetRole,
            UpdatedAt = now
        }, cancellationToken);
        securityAuditService.Write("LocalUserRoleChanged", "Success", ("UserId", user.UserId), ("Username", user.Username), ("Role", targetRole));

        return LocalUserCommandResult.Success($"User '{user.Username}' role changed to {targetRole}.");
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
        securityAuditService.Write("LocalUserStateChanged", "Success", ("UserId", user.UserId), ("Username", user.Username), ("IsActive", isActive));

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
        securityAuditService.Write("LocalUserDeleted", "Success", ("UserId", user.UserId), ("Username", user.Username));
        return LocalUserCommandResult.Success($"User '{user.Username}' deleted.");
    }

    private async Task<LocalUserCommandResult?> ValidateAdminProtectionAsync(LocalUserRecord user, CancellationToken cancellationToken)
    {
        if (!user.IsActive ||
            (!string.Equals(user.Role, SecurityRoles.Admin, StringComparison.OrdinalIgnoreCase) &&
             !string.Equals(user.Role, SecurityRoles.BreakGlassAdmin, StringComparison.OrdinalIgnoreCase)))
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

    private static string? NormalizeManagedRole(string role)
    {
        if (string.Equals(role, SecurityRoles.Viewer, StringComparison.OrdinalIgnoreCase))
        {
            return SecurityRoles.Viewer;
        }

        if (string.Equals(role, SecurityRoles.Operator, StringComparison.OrdinalIgnoreCase))
        {
            return SecurityRoles.Operator;
        }

        if (string.Equals(role, SecurityRoles.Admin, StringComparison.OrdinalIgnoreCase))
        {
            return SecurityRoles.Admin;
        }

        return null;
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

    private LocalUserRecord ApplyFailedLogin(LocalUserRecord user, DateTimeOffset now)
    {
        var failedLoginCount = user.FailedLoginCount + 1;
        DateTimeOffset? lockoutEndAt = failedLoginCount >= Math.Max(1, options.Value.LocalBreakGlass.MaxFailedAttempts)
            ? now.AddMinutes(Math.Max(1, options.Value.LocalBreakGlass.LockoutMinutes))
            : null;

        return user with
        {
            FailedLoginCount = failedLoginCount,
            LockoutEndAt = lockoutEndAt,
            UpdatedAt = now
        };
    }
}

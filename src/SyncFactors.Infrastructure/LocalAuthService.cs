using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;

namespace SyncFactors.Infrastructure;

public sealed class LocalAuthService(
    ILocalUserStore userStore,
    IPasswordHasher<LocalUserRecord> passwordHasher,
    IOptions<LocalAuthOptions> options,
    TimeProvider timeProvider) : ILocalAuthService
{
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
        return verification == PasswordVerificationResult.Failed
            ? LocalAuthenticationResult.Failed
            : new LocalAuthenticationResult(true, user);
    }

    public Task RecordSuccessfulLoginAsync(string userId, CancellationToken cancellationToken) =>
        userStore.UpdateLastLoginAsync(userId, timeProvider.GetUtcNow(), cancellationToken);
}

namespace SyncFactors.Infrastructure;

public sealed record LocalUserRecord(
    string UserId,
    string Username,
    string NormalizedUsername,
    string PasswordHash,
    string Role,
    bool IsActive,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    DateTimeOffset? LastLoginAt);

public sealed class LocalAuthOptions
{
    public BootstrapAdminOptions BootstrapAdmin { get; set; } = new();
}

public sealed class BootstrapAdminOptions
{
    public string? Username { get; set; }

    public string? Password { get; set; }
}

public sealed record LocalAuthenticationResult(
    bool Succeeded,
    LocalUserRecord? User)
{
    public static LocalAuthenticationResult Failed { get; } = new(false, null);
}

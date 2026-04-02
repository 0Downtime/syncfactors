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

public sealed record LocalUserSummary(
    string UserId,
    string Username,
    string Role,
    bool IsActive,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    DateTimeOffset? LastLoginAt);

public sealed record LocalUserCommandResult(
    bool Succeeded,
    string Message)
{
    public static LocalUserCommandResult Success(string message) => new(true, message);

    public static LocalUserCommandResult Failure(string message) => new(false, message);
}

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

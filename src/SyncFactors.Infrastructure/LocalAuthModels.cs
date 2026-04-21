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
    DateTimeOffset? LastLoginAt,
    int FailedLoginCount,
    DateTimeOffset? LockoutEndAt);

public sealed record LocalUserSummary(
    string UserId,
    string Username,
    string Role,
    bool IsActive,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    DateTimeOffset? LastLoginAt,
    int FailedLoginCount,
    DateTimeOffset? LockoutEndAt);

public sealed record LocalUserCommandResult(
    bool Succeeded,
    string Message)
{
    public static LocalUserCommandResult Success(string message) => new(true, message);

    public static LocalUserCommandResult Failure(string message) => new(false, message);
}

public sealed class LocalAuthOptions
{
    public const int MinAbsoluteSessionHours = 8;
    public const int MaxAbsoluteSessionHours = 720;
    public const int MinIdleTimeoutMinutes = 15;
    public const int MaxIdleTimeoutMinutes = 1440;
    public const int MinRememberMeSessionHours = 1;
    public const int MaxRememberMeSessionHours = 2160;

    public string Mode { get; set; } = "local-break-glass";

    public int AbsoluteSessionHours { get; set; } = 168;

    public int IdleTimeoutMinutes { get; set; } = 480;

    public int RememberMeSessionHours { get; set; } = 720;

    public bool AllowRememberMe { get; set; }

    public BootstrapAdminOptions BootstrapAdmin { get; set; } = new();

    public LocalBreakGlassOptions LocalBreakGlass { get; set; } = new();

    public OidcOptions Oidc { get; set; } = new();

    public TimeSpan GetAbsoluteSessionLifetime() =>
        TimeSpan.FromHours(Math.Clamp(AbsoluteSessionHours, MinAbsoluteSessionHours, MaxAbsoluteSessionHours));

    public TimeSpan GetIdleTimeout() =>
        TimeSpan.FromMinutes(Math.Clamp(IdleTimeoutMinutes, MinIdleTimeoutMinutes, MaxIdleTimeoutMinutes));

    public TimeSpan GetRememberMeSessionLifetime() =>
        TimeSpan.FromHours(Math.Clamp(RememberMeSessionHours, MinRememberMeSessionHours, MaxRememberMeSessionHours));
}

public sealed class BootstrapAdminOptions
{
    public string? Username { get; set; }

    public string? Password { get; set; }
}

public sealed class LocalBreakGlassOptions
{
    public bool Enabled { get; set; }

    public int MaxFailedAttempts { get; set; } = 5;

    public int LockoutMinutes { get; set; } = 15;
}

public sealed class OidcOptions
{
    public string? Authority { get; set; }

    public string? ClientId { get; set; }

    public string? ClientSecret { get; set; }

    public string CallbackPath { get; set; } = "/signin-oidc";

    public string SignedOutCallbackPath { get; set; } = "/signout-callback-oidc";

    public string RolesClaimType { get; set; } = "groups";

    public string DisplayNameClaimType { get; set; } = "name";

    public string UsernameClaimType { get; set; } = "preferred_username";

    public string[] ViewerGroups { get; set; } = [];

    public string[] OperatorGroups { get; set; } = [];

    public string[] AdminGroups { get; set; } = [];
}

public sealed record LocalAuthenticationResult(
    bool Succeeded,
    LocalUserRecord? User,
    string? FailureMessage = null)
{
    public static LocalAuthenticationResult Failed { get; } = new(false, null, null);
}

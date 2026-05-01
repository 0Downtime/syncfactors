namespace SyncFactors.Infrastructure;

public interface ILocalUserStore
{
    Task<bool> AnyUsersAsync(CancellationToken cancellationToken);

    Task<IReadOnlyList<LocalUserSummary>> ListUsersAsync(CancellationToken cancellationToken);

    Task<LocalUserRecord?> FindByIdAsync(string userId, CancellationToken cancellationToken);

    Task<LocalUserRecord?> FindByUsernameAsync(string username, CancellationToken cancellationToken);

    Task<int> CountActiveAdminsAsync(CancellationToken cancellationToken);

    Task CreateAsync(LocalUserRecord user, CancellationToken cancellationToken);

    Task UpdateAsync(LocalUserRecord user, CancellationToken cancellationToken);

    Task UpdateLastLoginAsync(string userId, DateTimeOffset lastLoginAt, CancellationToken cancellationToken);

    Task DeleteAsync(string userId, CancellationToken cancellationToken);
}

public interface ILocalAuthService
{
    Task EnsureBootstrapAdminAsync(CancellationToken cancellationToken);

    bool IsLocalAuthenticationEnabled { get; }

    Task<LocalAuthenticationResult> AuthenticateAsync(string username, string password, CancellationToken cancellationToken);

    Task RecordSuccessfulLoginAsync(string userId, CancellationToken cancellationToken);

    Task<IReadOnlyList<LocalUserSummary>> ListUsersAsync(CancellationToken cancellationToken);

    Task<LocalUserRecord?> FindUserByIdAsync(string userId, CancellationToken cancellationToken);

    Task<LocalUserCommandResult> CreateUserAsync(string username, string password, string role, CancellationToken cancellationToken);

    Task<LocalUserCommandResult> ResetPasswordAsync(string userId, string newPassword, CancellationToken cancellationToken);

    Task<LocalUserCommandResult> SetUserRoleAsync(string userId, string role, string actingUserId, CancellationToken cancellationToken);

    Task<LocalUserCommandResult> SetUserActiveStateAsync(string userId, bool isActive, string actingUserId, CancellationToken cancellationToken);

    Task<LocalUserCommandResult> DeleteUserAsync(string userId, string actingUserId, CancellationToken cancellationToken);
}

public interface IOidcAccountStore
{
    Task UpsertAsync(OidcAccountRecord account, CancellationToken cancellationToken);

    Task<IReadOnlyList<OidcAccountRecord>> ListAccountsAsync(CancellationToken cancellationToken);
}

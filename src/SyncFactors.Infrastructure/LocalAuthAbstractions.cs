namespace SyncFactors.Infrastructure;

public interface ILocalUserStore
{
    Task<bool> AnyUsersAsync(CancellationToken cancellationToken);

    Task<LocalUserRecord?> FindByUsernameAsync(string username, CancellationToken cancellationToken);

    Task CreateAsync(LocalUserRecord user, CancellationToken cancellationToken);

    Task UpdateLastLoginAsync(string userId, DateTimeOffset lastLoginAt, CancellationToken cancellationToken);
}

public interface ILocalAuthService
{
    Task EnsureBootstrapAdminAsync(CancellationToken cancellationToken);

    Task<LocalAuthenticationResult> AuthenticateAsync(string username, string password, CancellationToken cancellationToken);

    Task RecordSuccessfulLoginAsync(string userId, CancellationToken cancellationToken);
}

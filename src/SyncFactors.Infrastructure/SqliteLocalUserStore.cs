using Microsoft.Data.Sqlite;

namespace SyncFactors.Infrastructure;

public sealed class SqliteLocalUserStore(SqlitePathResolver pathResolver) : ILocalUserStore
{
    public async Task<bool> AnyUsersAsync(CancellationToken cancellationToken)
    {
        var databasePath = pathResolver.ResolveConfiguredPath();
        if (string.IsNullOrWhiteSpace(databasePath))
        {
            return false;
        }

        await using var connection = await OpenConnectionAsync(databasePath, cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT 1 FROM local_users LIMIT 1;";
        var result = await command.ExecuteScalarAsync(cancellationToken);
        return result is not null;
    }

    public async Task<LocalUserRecord?> FindByUsernameAsync(string username, CancellationToken cancellationToken)
    {
        var databasePath = pathResolver.ResolveConfiguredPath();
        if (string.IsNullOrWhiteSpace(databasePath) || string.IsNullOrWhiteSpace(username))
        {
            return null;
        }

        await using var connection = await OpenConnectionAsync(databasePath, cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT user_id, username, normalized_username, password_hash, role, is_active, created_at, updated_at, last_login_at, failed_login_count, lockout_end_at
            FROM local_users
            WHERE normalized_username = $normalizedUsername
            LIMIT 1;
            """;
        command.Parameters.AddWithValue("$normalizedUsername", NormalizeUsername(username));

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken)
            ? MapUser(reader)
            : null;
    }

    public async Task<IReadOnlyList<LocalUserSummary>> ListUsersAsync(CancellationToken cancellationToken)
    {
        var databasePath = pathResolver.ResolveConfiguredPath();
        if (string.IsNullOrWhiteSpace(databasePath))
        {
            return [];
        }

        await using var connection = await OpenConnectionAsync(databasePath, cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT user_id, username, role, is_active, created_at, updated_at, last_login_at, failed_login_count, lockout_end_at
            FROM local_users
            ORDER BY normalized_username ASC;
            """;

        var users = new List<LocalUserSummary>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            users.Add(new LocalUserSummary(
                UserId: reader.GetString(0),
                Username: reader.GetString(1),
                Role: reader.GetString(2),
                IsActive: reader.GetInt64(3) != 0,
                CreatedAt: DateTimeOffset.Parse(reader.GetString(4)),
                UpdatedAt: DateTimeOffset.Parse(reader.GetString(5)),
                LastLoginAt: reader.IsDBNull(6) ? null : DateTimeOffset.Parse(reader.GetString(6)),
                FailedLoginCount: reader.GetInt32(7),
                LockoutEndAt: reader.IsDBNull(8) ? null : DateTimeOffset.Parse(reader.GetString(8))));
        }

        return users;
    }

    public async Task<LocalUserRecord?> FindByIdAsync(string userId, CancellationToken cancellationToken)
    {
        var databasePath = pathResolver.ResolveConfiguredPath();
        if (string.IsNullOrWhiteSpace(databasePath) || string.IsNullOrWhiteSpace(userId))
        {
            return null;
        }

        await using var connection = await OpenConnectionAsync(databasePath, cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT user_id, username, normalized_username, password_hash, role, is_active, created_at, updated_at, last_login_at, failed_login_count, lockout_end_at
            FROM local_users
            WHERE user_id = $userId
            LIMIT 1;
            """;
        command.Parameters.AddWithValue("$userId", userId);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken)
            ? MapUser(reader)
            : null;
    }

    public async Task<int> CountActiveAdminsAsync(CancellationToken cancellationToken)
    {
        var databasePath = pathResolver.ResolveConfiguredPath();
        if (string.IsNullOrWhiteSpace(databasePath))
        {
            return 0;
        }

        await using var connection = await OpenConnectionAsync(databasePath, cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT COUNT(*)
            FROM local_users
            WHERE (role = 'Admin' OR role = 'BreakGlassAdmin') AND is_active = 1;
            """;
        var result = await command.ExecuteScalarAsync(cancellationToken);
        return Convert.ToInt32(result ?? 0);
    }

    public async Task CreateAsync(LocalUserRecord user, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(user);

        var databasePath = pathResolver.ResolveConfiguredPath();
        if (string.IsNullOrWhiteSpace(databasePath))
        {
            throw new InvalidOperationException("SyncFactors SQLite path is not configured.");
        }

        await using var connection = await OpenConnectionAsync(databasePath, cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT INTO local_users (
              user_id,
              username,
              normalized_username,
              password_hash,
              role,
              is_active,
              created_at,
              updated_at,
              last_login_at,
              failed_login_count,
              lockout_end_at
            )
            VALUES (
              $userId,
              $username,
              $normalizedUsername,
              $passwordHash,
              $role,
              $isActive,
              $createdAt,
              $updatedAt,
              $lastLoginAt,
              $failedLoginCount,
              $lockoutEndAt
            );
            """;
        command.Parameters.AddWithValue("$userId", user.UserId);
        command.Parameters.AddWithValue("$username", user.Username);
        command.Parameters.AddWithValue("$normalizedUsername", user.NormalizedUsername);
        command.Parameters.AddWithValue("$passwordHash", user.PasswordHash);
        command.Parameters.AddWithValue("$role", user.Role);
        command.Parameters.AddWithValue("$isActive", user.IsActive ? 1 : 0);
        command.Parameters.AddWithValue("$createdAt", user.CreatedAt.ToString("O"));
        command.Parameters.AddWithValue("$updatedAt", user.UpdatedAt.ToString("O"));
        command.Parameters.AddWithValue("$lastLoginAt", (object?)user.LastLoginAt?.ToString("O") ?? DBNull.Value);
        command.Parameters.AddWithValue("$failedLoginCount", user.FailedLoginCount);
        command.Parameters.AddWithValue("$lockoutEndAt", (object?)user.LockoutEndAt?.ToString("O") ?? DBNull.Value);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task UpdateLastLoginAsync(string userId, DateTimeOffset lastLoginAt, CancellationToken cancellationToken)
    {
        var databasePath = pathResolver.ResolveConfiguredPath();
        if (string.IsNullOrWhiteSpace(databasePath) || string.IsNullOrWhiteSpace(userId))
        {
            return;
        }

        await using var connection = await OpenConnectionAsync(databasePath, cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            UPDATE local_users
            SET last_login_at = $lastLoginAt,
                updated_at = $updatedAt,
                failed_login_count = 0,
                lockout_end_at = NULL
            WHERE user_id = $userId;
            """;
        command.Parameters.AddWithValue("$userId", userId);
        command.Parameters.AddWithValue("$lastLoginAt", lastLoginAt.ToString("O"));
        command.Parameters.AddWithValue("$updatedAt", lastLoginAt.ToString("O"));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task UpdateAsync(LocalUserRecord user, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(user);

        var databasePath = pathResolver.ResolveConfiguredPath();
        if (string.IsNullOrWhiteSpace(databasePath))
        {
            throw new InvalidOperationException("SyncFactors SQLite path is not configured.");
        }

        await using var connection = await OpenConnectionAsync(databasePath, cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            UPDATE local_users
            SET username = $username,
                normalized_username = $normalizedUsername,
                password_hash = $passwordHash,
                role = $role,
                is_active = $isActive,
                updated_at = $updatedAt,
                last_login_at = $lastLoginAt,
                failed_login_count = $failedLoginCount,
                lockout_end_at = $lockoutEndAt
            WHERE user_id = $userId;
            """;
        command.Parameters.AddWithValue("$userId", user.UserId);
        command.Parameters.AddWithValue("$username", user.Username);
        command.Parameters.AddWithValue("$normalizedUsername", user.NormalizedUsername);
        command.Parameters.AddWithValue("$passwordHash", user.PasswordHash);
        command.Parameters.AddWithValue("$role", user.Role);
        command.Parameters.AddWithValue("$isActive", user.IsActive ? 1 : 0);
        command.Parameters.AddWithValue("$updatedAt", user.UpdatedAt.ToString("O"));
        command.Parameters.AddWithValue("$lastLoginAt", (object?)user.LastLoginAt?.ToString("O") ?? DBNull.Value);
        command.Parameters.AddWithValue("$failedLoginCount", user.FailedLoginCount);
        command.Parameters.AddWithValue("$lockoutEndAt", (object?)user.LockoutEndAt?.ToString("O") ?? DBNull.Value);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task DeleteAsync(string userId, CancellationToken cancellationToken)
    {
        var databasePath = pathResolver.ResolveConfiguredPath();
        if (string.IsNullOrWhiteSpace(databasePath) || string.IsNullOrWhiteSpace(userId))
        {
            return;
        }

        await using var connection = await OpenConnectionAsync(databasePath, cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM local_users WHERE user_id = $userId;";
        command.Parameters.AddWithValue("$userId", userId);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public static string NormalizeUsername(string username) =>
        username.Trim().ToUpperInvariant();

    private static LocalUserRecord MapUser(SqliteDataReader reader)
    {
        return new LocalUserRecord(
            UserId: reader.GetString(0),
            Username: reader.GetString(1),
            NormalizedUsername: reader.GetString(2),
            PasswordHash: reader.GetString(3),
            Role: reader.GetString(4),
            IsActive: reader.GetInt64(5) != 0,
            CreatedAt: DateTimeOffset.Parse(reader.GetString(6)),
            UpdatedAt: DateTimeOffset.Parse(reader.GetString(7)),
            LastLoginAt: reader.IsDBNull(8) ? null : DateTimeOffset.Parse(reader.GetString(8)),
            FailedLoginCount: reader.GetInt32(9),
            LockoutEndAt: reader.IsDBNull(10) ? null : DateTimeOffset.Parse(reader.GetString(10)));
    }

    private static async Task<SqliteConnection> OpenConnectionAsync(string databasePath, CancellationToken cancellationToken)
    {
        var connection = new SqliteConnection($"Data Source={databasePath}");
        await connection.OpenAsync(cancellationToken);
        return connection;
    }
}

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
            SELECT user_id, username, normalized_username, password_hash, role, is_active, created_at, updated_at, last_login_at
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
              last_login_at
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
              $lastLoginAt
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
                updated_at = $updatedAt
            WHERE user_id = $userId;
            """;
        command.Parameters.AddWithValue("$userId", userId);
        command.Parameters.AddWithValue("$lastLoginAt", lastLoginAt.ToString("O"));
        command.Parameters.AddWithValue("$updatedAt", lastLoginAt.ToString("O"));
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
            LastLoginAt: reader.IsDBNull(8) ? null : DateTimeOffset.Parse(reader.GetString(8)));
    }

    private static async Task<SqliteConnection> OpenConnectionAsync(string databasePath, CancellationToken cancellationToken)
    {
        var connection = new SqliteConnection($"Data Source={databasePath}");
        await connection.OpenAsync(cancellationToken);
        return connection;
    }
}

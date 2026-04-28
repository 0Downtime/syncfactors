using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace SyncFactors.Infrastructure;

public sealed class SqliteOidcAccountStore(SqlitePathResolver pathResolver) : IOidcAccountStore
{
    public async Task UpsertAsync(OidcAccountRecord account, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(account);

        var databasePath = pathResolver.ResolveConfiguredPath();
        if (string.IsNullOrWhiteSpace(databasePath) || string.IsNullOrWhiteSpace(account.Subject))
        {
            return;
        }

        await using var connection = await OpenConnectionAsync(databasePath, cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT INTO oidc_accounts (
              subject,
              username,
              display_name,
              access_level,
              groups_json,
              first_seen_at,
              last_login_at
            )
            VALUES (
              $subject,
              $username,
              $displayName,
              $accessLevel,
              $groupsJson,
              $firstSeenAt,
              $lastLoginAt
            )
            ON CONFLICT(subject) DO UPDATE SET
              username = excluded.username,
              display_name = excluded.display_name,
              access_level = excluded.access_level,
              groups_json = excluded.groups_json,
              last_login_at = excluded.last_login_at;
            """;
        command.Parameters.AddWithValue("$subject", account.Subject);
        command.Parameters.AddWithValue("$username", account.Username);
        command.Parameters.AddWithValue("$displayName", (object?)account.DisplayName ?? DBNull.Value);
        command.Parameters.AddWithValue("$accessLevel", account.AccessLevel);
        command.Parameters.AddWithValue("$groupsJson", JsonSerializer.Serialize(account.Groups, JsonOptions.Default));
        command.Parameters.AddWithValue("$firstSeenAt", account.FirstSeenAt.ToString("O"));
        command.Parameters.AddWithValue("$lastLoginAt", account.LastLoginAt.ToString("O"));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<OidcAccountRecord>> ListAccountsAsync(CancellationToken cancellationToken)
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
            SELECT subject, username, display_name, access_level, groups_json, first_seen_at, last_login_at
            FROM oidc_accounts
            ORDER BY username ASC, subject ASC;
            """;

        var accounts = new List<OidcAccountRecord>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            accounts.Add(new OidcAccountRecord(
                Subject: reader.GetString(0),
                Username: reader.GetString(1),
                DisplayName: reader.IsDBNull(2) ? null : reader.GetString(2),
                AccessLevel: reader.GetString(3),
                Groups: ParseGroups(reader.GetString(4)),
                FirstSeenAt: DateTimeOffset.Parse(reader.GetString(5)),
                LastLoginAt: DateTimeOffset.Parse(reader.GetString(6))));
        }

        return accounts;
    }

    private static IReadOnlyList<string> ParseGroups(string groupsJson)
    {
        try
        {
            return JsonSerializer.Deserialize<string[]>(groupsJson, JsonOptions.Default) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private static async Task<SqliteConnection> OpenConnectionAsync(string databasePath, CancellationToken cancellationToken)
    {
        var connection = new SqliteConnection($"Data Source={databasePath}");
        await connection.OpenAsync(cancellationToken);
        return connection;
    }
}

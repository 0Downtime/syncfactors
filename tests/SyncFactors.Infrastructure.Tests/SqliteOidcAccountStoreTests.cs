using Microsoft.Data.Sqlite;
using SyncFactors.Infrastructure;

namespace SyncFactors.Infrastructure.Tests;

public sealed class SqliteOidcAccountStoreTests
{
    [Fact]
    public async Task InitializeAsync_CreatesOidcAccountsTable()
    {
        var databasePath = Path.Combine(Path.GetTempPath(), $"syncfactors-oidc-schema-{Guid.NewGuid():N}.db");

        try
        {
            var pathResolver = new SqlitePathResolver(databasePath);
            await new SqliteDatabaseInitializer(pathResolver).InitializeAsync(CancellationToken.None);

            await using var connection = new SqliteConnection($"Data Source={databasePath};Mode=ReadOnly");
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = "PRAGMA table_info(oidc_accounts);";
            await using var reader = await command.ExecuteReaderAsync();

            var columns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            while (await reader.ReadAsync())
            {
                columns.Add(reader.GetString(1));
            }

            Assert.Contains("subject", columns);
            Assert.Contains("groups_json", columns);
            Assert.Contains("access_level", columns);
        }
        finally
        {
            File.Delete(databasePath);
        }
    }

    [Fact]
    public async Task UpsertAsync_PersistsLatestOidcAccount()
    {
        var databasePath = Path.Combine(Path.GetTempPath(), $"syncfactors-oidc-store-{Guid.NewGuid():N}.db");

        try
        {
            var pathResolver = new SqlitePathResolver(databasePath);
            await new SqliteDatabaseInitializer(pathResolver).InitializeAsync(CancellationToken.None);
            IOidcAccountStore store = new SqliteOidcAccountStore(pathResolver);

            await store.UpsertAsync(
                new OidcAccountRecord(
                    Subject: "subject-1",
                    Username: "alice@example.com",
                    DisplayName: "Alice",
                    AccessLevel: "Viewer",
                    Groups: ["sync-viewers"],
                    FirstSeenAt: DateTimeOffset.Parse("2026-04-01T10:00:00Z"),
                    LastLoginAt: DateTimeOffset.Parse("2026-04-01T10:00:00Z")),
                CancellationToken.None);
            await store.UpsertAsync(
                new OidcAccountRecord(
                    Subject: "subject-1",
                    Username: "alice.admin@example.com",
                    DisplayName: "Alice Admin",
                    AccessLevel: "Admin",
                    Groups: ["sync-admins", "sync-viewers"],
                    FirstSeenAt: DateTimeOffset.Parse("2026-04-02T10:00:00Z"),
                    LastLoginAt: DateTimeOffset.Parse("2026-04-02T10:00:00Z")),
                CancellationToken.None);

            var account = Assert.Single(await store.ListAccountsAsync(CancellationToken.None));
            Assert.Equal("alice.admin@example.com", account.Username);
            Assert.Equal("Admin", account.AccessLevel);
            Assert.Equal(DateTimeOffset.Parse("2026-04-01T10:00:00Z"), account.FirstSeenAt);
            Assert.Equal(["sync-admins", "sync-viewers"], account.Groups);
        }
        finally
        {
            File.Delete(databasePath);
        }
    }
}

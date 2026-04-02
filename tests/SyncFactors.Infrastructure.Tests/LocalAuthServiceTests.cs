using Microsoft.AspNetCore.Identity;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;
using SyncFactors.Infrastructure;

namespace SyncFactors.Infrastructure.Tests;

public sealed class LocalAuthServiceTests
{
    [Fact]
    public async Task EnsureBootstrapAdminAsync_CreatesBootstrapUserAndIsIdempotent()
    {
        var databasePath = Path.Combine(Path.GetTempPath(), $"syncfactors-auth-{Guid.NewGuid():N}.db");

        try
        {
            var store = await CreateStoreAsync(databasePath);
            var service = CreateService(
                store,
                username: "admin",
                password: "Password123!");

            await service.EnsureBootstrapAdminAsync(CancellationToken.None);
            await service.EnsureBootstrapAdminAsync(CancellationToken.None);

            var user = await store.FindByUsernameAsync("admin", CancellationToken.None);
            Assert.NotNull(user);
            Assert.True(user!.IsActive);

            await using var connection = new SqliteConnection($"Data Source={databasePath}");
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT COUNT(*) FROM local_users;";
            var count = Convert.ToInt32(await command.ExecuteScalarAsync());
            Assert.Equal(1, count);
        }
        finally
        {
            File.Delete(databasePath);
        }
    }

    [Fact]
    public async Task EnsureBootstrapAdminAsync_ThrowsWhenNoUsersExistAndCredentialsAreMissing()
    {
        var databasePath = Path.Combine(Path.GetTempPath(), $"syncfactors-auth-missing-{Guid.NewGuid():N}.db");

        try
        {
            var store = await CreateStoreAsync(databasePath);
            var service = CreateService(store, username: null, password: null);

            await Assert.ThrowsAsync<InvalidOperationException>(() => service.EnsureBootstrapAdminAsync(CancellationToken.None));
        }
        finally
        {
            File.Delete(databasePath);
        }
    }

    [Fact]
    public async Task AuthenticateAsync_AcceptsValidPasswordAndRejectsBadPassword()
    {
        var databasePath = Path.Combine(Path.GetTempPath(), $"syncfactors-auth-verify-{Guid.NewGuid():N}.db");

        try
        {
            var store = await CreateStoreAsync(databasePath);
            var service = CreateService(store, username: "admin", password: "Password123!");
            await service.EnsureBootstrapAdminAsync(CancellationToken.None);

            var success = await service.AuthenticateAsync("admin", "Password123!", CancellationToken.None);
            var failure = await service.AuthenticateAsync("admin", "wrong", CancellationToken.None);

            Assert.True(success.Succeeded);
            Assert.Equal("admin", success.User?.Username);
            Assert.False(failure.Succeeded);
        }
        finally
        {
            File.Delete(databasePath);
        }
    }

    [Fact]
    public async Task SqliteLocalUserStore_NormalizesUsernamesAndPersistsLastLogin()
    {
        var databasePath = Path.Combine(Path.GetTempPath(), $"syncfactors-auth-store-{Guid.NewGuid():N}.db");

        try
        {
            var store = await CreateStoreAsync(databasePath);
            var user = new LocalUserRecord(
                UserId: "user-1",
                Username: "Admin",
                NormalizedUsername: SqliteLocalUserStore.NormalizeUsername("Admin"),
                PasswordHash: "hash",
                Role: "Admin",
                IsActive: true,
                CreatedAt: DateTimeOffset.UtcNow,
                UpdatedAt: DateTimeOffset.UtcNow,
                LastLoginAt: null);

            await store.CreateAsync(user, CancellationToken.None);
            await store.UpdateLastLoginAsync("user-1", DateTimeOffset.Parse("2026-04-02T15:00:00Z"), CancellationToken.None);

            var loaded = await store.FindByUsernameAsync(" admin ", CancellationToken.None);

            Assert.NotNull(loaded);
            Assert.Equal("ADMIN", loaded!.NormalizedUsername);
            Assert.Equal(DateTimeOffset.Parse("2026-04-02T15:00:00Z"), loaded.LastLoginAt);
        }
        finally
        {
            File.Delete(databasePath);
        }
    }

    private static LocalAuthService CreateService(ILocalUserStore store, string? username, string? password)
    {
        return new LocalAuthService(
            store,
            new PasswordHasher<LocalUserRecord>(),
            Options.Create(new LocalAuthOptions
            {
                BootstrapAdmin = new BootstrapAdminOptions
                {
                    Username = username,
                    Password = password
                }
            }),
            TimeProvider.System);
    }

    private static async Task<SqliteLocalUserStore> CreateStoreAsync(string databasePath)
    {
        var pathResolver = new SqlitePathResolver(databasePath);
        var initializer = new SqliteDatabaseInitializer(pathResolver);
        await initializer.InitializeAsync(CancellationToken.None);
        return new SqliteLocalUserStore(pathResolver);
    }
}

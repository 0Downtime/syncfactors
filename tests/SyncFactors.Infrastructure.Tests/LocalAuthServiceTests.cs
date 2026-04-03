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
                LastLoginAt: null,
                FailedLoginCount: 0,
                LockoutEndAt: null);

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

    [Fact]
    public async Task CreateUserAsync_CreatesOperatorByDefault()
    {
        var databasePath = Path.Combine(Path.GetTempPath(), $"syncfactors-auth-create-{Guid.NewGuid():N}.db");

        try
        {
            var store = await CreateStoreAsync(databasePath);
            var service = CreateService(store, username: "admin", password: "Password123!");
            await service.EnsureBootstrapAdminAsync(CancellationToken.None);

            var result = await service.CreateUserAsync("alice", "Password1234", isAdmin: false, CancellationToken.None);
            var user = await store.FindByUsernameAsync("alice", CancellationToken.None);

            Assert.True(result.Succeeded);
            Assert.NotNull(user);
            Assert.Equal("Operator", user!.Role);
        }
        finally
        {
            File.Delete(databasePath);
        }
    }

    [Fact]
    public async Task ResetPasswordAsync_UpdatesStoredHash()
    {
        var databasePath = Path.Combine(Path.GetTempPath(), $"syncfactors-auth-reset-{Guid.NewGuid():N}.db");

        try
        {
            var store = await CreateStoreAsync(databasePath);
            var service = CreateService(store, username: "admin", password: "Password123!");
            await service.EnsureBootstrapAdminAsync(CancellationToken.None);
            var before = await store.FindByUsernameAsync("admin", CancellationToken.None);

            var result = await service.ResetPasswordAsync(before!.UserId, "NewPassword1234", CancellationToken.None);
            var after = await store.FindByUsernameAsync("admin", CancellationToken.None);
            var auth = await service.AuthenticateAsync("admin", "NewPassword1234", CancellationToken.None);

            Assert.True(result.Succeeded);
            Assert.NotEqual(before.PasswordHash, after!.PasswordHash);
            Assert.True(auth.Succeeded);
        }
        finally
        {
            File.Delete(databasePath);
        }
    }

    [Fact]
    public async Task SetUserRoleAsync_PromotesOperatorToAdmin()
    {
        var databasePath = Path.Combine(Path.GetTempPath(), $"syncfactors-auth-role-up-{Guid.NewGuid():N}.db");

        try
        {
            var store = await CreateStoreAsync(databasePath);
            var service = CreateService(store, username: "admin", password: "Password123!");
            await service.EnsureBootstrapAdminAsync(CancellationToken.None);
            await service.CreateUserAsync("alice", "Password1234", isAdmin: false, CancellationToken.None);
            var user = await store.FindByUsernameAsync("alice", CancellationToken.None);

            var result = await service.SetUserRoleAsync(user!.UserId, true, "admin-id", CancellationToken.None);
            var updated = await store.FindByUsernameAsync("alice", CancellationToken.None);

            Assert.True(result.Succeeded);
            Assert.Equal("Admin", updated!.Role);
        }
        finally
        {
            File.Delete(databasePath);
        }
    }

    [Fact]
    public async Task SetUserRoleAsync_ProtectsLastActiveAdminFromDemotion()
    {
        var databasePath = Path.Combine(Path.GetTempPath(), $"syncfactors-auth-role-down-{Guid.NewGuid():N}.db");

        try
        {
            var store = await CreateStoreAsync(databasePath);
            var service = CreateService(store, username: "admin", password: "Password123!");
            await service.EnsureBootstrapAdminAsync(CancellationToken.None);
            var admin = await store.FindByUsernameAsync("admin", CancellationToken.None);

            var result = await service.SetUserRoleAsync(admin!.UserId, false, "someone-else", CancellationToken.None);

            Assert.False(result.Succeeded);
            Assert.Equal("At least one active admin account must remain.", result.Message);
        }
        finally
        {
            File.Delete(databasePath);
        }
    }

    [Fact]
    public async Task SetUserActiveStateAsync_ProtectsLastActiveAdmin()
    {
        var databasePath = Path.Combine(Path.GetTempPath(), $"syncfactors-auth-protect-{Guid.NewGuid():N}.db");

        try
        {
            var store = await CreateStoreAsync(databasePath);
            var service = CreateService(store, username: "admin", password: "Password123!");
            await service.EnsureBootstrapAdminAsync(CancellationToken.None);
            var admin = await store.FindByUsernameAsync("admin", CancellationToken.None);

            var result = await service.SetUserActiveStateAsync(admin!.UserId, false, "someone-else", CancellationToken.None);

            Assert.False(result.Succeeded);
            Assert.Equal("At least one active admin account must remain.", result.Message);
        }
        finally
        {
            File.Delete(databasePath);
        }
    }

    [Fact]
    public async Task DeleteUserAsync_DeletesNonAdminUsers()
    {
        var databasePath = Path.Combine(Path.GetTempPath(), $"syncfactors-auth-delete-{Guid.NewGuid():N}.db");

        try
        {
            var store = await CreateStoreAsync(databasePath);
            var service = CreateService(store, username: "admin", password: "Password123!");
            await service.EnsureBootstrapAdminAsync(CancellationToken.None);
            await service.CreateUserAsync("alice", "Password1234", isAdmin: false, CancellationToken.None);
            var user = await store.FindByUsernameAsync("alice", CancellationToken.None);

            var result = await service.DeleteUserAsync(user!.UserId, "admin-id", CancellationToken.None);

            Assert.True(result.Succeeded);
            Assert.Null(await store.FindByUsernameAsync("alice", CancellationToken.None));
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
                Mode = "local-break-glass",
                BootstrapAdmin = new BootstrapAdminOptions
                {
                    Username = username,
                    Password = password
                },
                LocalBreakGlass = new LocalBreakGlassOptions
                {
                    Enabled = true,
                    MaxFailedAttempts = 5,
                    LockoutMinutes = 15
                }
            }),
            TimeProvider.System,
            new NoOpSecurityAuditService());
    }

    private static async Task<SqliteLocalUserStore> CreateStoreAsync(string databasePath)
    {
        var pathResolver = new SqlitePathResolver(databasePath);
        var initializer = new SqliteDatabaseInitializer(pathResolver);
        await initializer.InitializeAsync(CancellationToken.None);
        return new SqliteLocalUserStore(pathResolver);
    }

    private sealed class NoOpSecurityAuditService : ISecurityAuditService
    {
        public void Write(string eventType, string outcome, params (string Key, object? Value)[] fields)
        {
            _ = eventType;
            _ = outcome;
            _ = fields;
        }
    }
}

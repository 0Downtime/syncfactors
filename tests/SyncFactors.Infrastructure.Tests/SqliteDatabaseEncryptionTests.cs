using Microsoft.Data.Sqlite;
using SyncFactors.Infrastructure;

namespace SyncFactors.Infrastructure.Tests;

public sealed class SqliteDatabaseEncryptionTests : IDisposable
{
    private const string PasswordEnvironmentVariable = "SYNCFACTORS_SQLITE_PASSWORD";
    private const string ConfigurationPasswordEnvironmentVariable = "SyncFactors__SqlitePassword";
    private readonly string? _originalPassword = Environment.GetEnvironmentVariable(PasswordEnvironmentVariable);
    private readonly string? _originalConfigurationPassword = Environment.GetEnvironmentVariable(ConfigurationPasswordEnvironmentVariable);

    public void Dispose()
    {
        Environment.SetEnvironmentVariable(PasswordEnvironmentVariable, _originalPassword);
        Environment.SetEnvironmentVariable(ConfigurationPasswordEnvironmentVariable, _originalConfigurationPassword);
    }

    [Fact]
    public async Task InitializeAsync_CreatesEncryptedDatabase_WhenPasswordIsConfigured()
    {
        var databasePath = Path.Combine(Path.GetTempPath(), $"syncfactors-encrypted-{Guid.NewGuid():N}.db");
        SetSqlitePassword("test-sqlcipher-password");

        try
        {
            await new SqliteDatabaseInitializer(new SqlitePathResolver(databasePath)).InitializeAsync(CancellationToken.None);
            Assert.False(HasPlaintextSqliteHeader(databasePath));

            await Assert.ThrowsAsync<SqliteException>(() => OpenAndReadSchemaVersionAsync(databasePath, password: null));

            var schemaVersionCount = await OpenAndReadSchemaVersionAsync(databasePath, "test-sqlcipher-password");
            Assert.True(schemaVersionCount > 0);
        }
        finally
        {
            DeleteDatabaseFiles(databasePath);
        }
    }

    [Fact]
    public async Task InitializeAsync_ConvertsPlaintextDatabase_WhenPasswordIsConfiguredLater()
    {
        var databasePath = Path.Combine(Path.GetTempPath(), $"syncfactors-plaintext-upgrade-{Guid.NewGuid():N}.db");

        try
        {
            SetSqlitePassword(null);
            await new SqliteDatabaseInitializer(new SqlitePathResolver(databasePath)).InitializeAsync(CancellationToken.None);
            Assert.True(HasPlaintextSqliteHeader(databasePath));
            Assert.True(await OpenAndReadSchemaVersionAsync(databasePath, password: null) > 0);

            SetSqlitePassword("test-sqlcipher-upgrade-password");
            await new SqliteDatabaseInitializer(new SqlitePathResolver(databasePath)).InitializeAsync(CancellationToken.None);
            Assert.False(HasPlaintextSqliteHeader(databasePath));

            await Assert.ThrowsAsync<SqliteException>(() => OpenAndReadSchemaVersionAsync(databasePath, password: null));
            Assert.True(await OpenAndReadSchemaVersionAsync(databasePath, "test-sqlcipher-upgrade-password") > 0);
            Assert.NotEmpty(Directory.EnumerateFiles(Path.GetDirectoryName(databasePath)!, $"{Path.GetFileName(databasePath)}.plaintext-*.bak"));
        }
        finally
        {
            DeleteDatabaseFiles(databasePath);
            foreach (var backupPath in Directory.EnumerateFiles(Path.GetDirectoryName(databasePath)!, $"{Path.GetFileName(databasePath)}.plaintext-*.bak*"))
            {
                File.Delete(backupPath);
            }
        }
    }

    [Fact]
    public async Task InitializeAsync_ReopensEncryptedDatabase_WhenPasswordIsStillConfigured()
    {
        var databasePath = Path.Combine(Path.GetTempPath(), $"syncfactors-encrypted-reopen-{Guid.NewGuid():N}.db");
        SetSqlitePassword("test-sqlcipher-reopen-password");

        try
        {
            var initializer = new SqliteDatabaseInitializer(new SqlitePathResolver(databasePath));

            await initializer.InitializeAsync(CancellationToken.None);
            await initializer.InitializeAsync(CancellationToken.None);

            Assert.False(HasPlaintextSqliteHeader(databasePath));
            Assert.True(await OpenAndReadSchemaVersionAsync(databasePath, "test-sqlcipher-reopen-password") > 0);
        }
        finally
        {
            DeleteDatabaseFiles(databasePath);
        }
    }

    [Fact]
    public async Task InitializeAsync_Throws_WhenEncryptedDatabasePasswordDoesNotMatch()
    {
        var databasePath = Path.Combine(Path.GetTempPath(), $"syncfactors-encrypted-wrong-key-{Guid.NewGuid():N}.db");

        try
        {
            SetSqlitePassword("test-sqlcipher-original-password");
            await new SqliteDatabaseInitializer(new SqlitePathResolver(databasePath)).InitializeAsync(CancellationToken.None);

            SetSqlitePassword("test-sqlcipher-wrong-password");
            var ex = await Assert.ThrowsAsync<InvalidOperationException>(
                () => new SqliteDatabaseInitializer(new SqlitePathResolver(databasePath)).InitializeAsync(CancellationToken.None));

            Assert.Contains("configured SQLCipher password", ex.Message, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            DeleteDatabaseFiles(databasePath);
        }
    }

    private static void SetSqlitePassword(string? password)
    {
        Environment.SetEnvironmentVariable(PasswordEnvironmentVariable, password);
        Environment.SetEnvironmentVariable(ConfigurationPasswordEnvironmentVariable, null);
    }

    private static async Task<long> OpenAndReadSchemaVersionAsync(string databasePath, string? password)
    {
        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false,
        };

        if (!string.IsNullOrWhiteSpace(password))
        {
            builder.Password = password;
        }

        await using var connection = new SqliteConnection(builder.ToString());
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM schema_versions;";
        return (long)(await command.ExecuteScalarAsync() ?? 0L);
    }

    private static void DeleteDatabaseFiles(string databasePath)
    {
        DeleteIfExists(databasePath);
        DeleteIfExists($"{databasePath}-wal");
        DeleteIfExists($"{databasePath}-shm");
    }

    private static void DeleteIfExists(string path)
    {
        if (File.Exists(path))
        {
            File.Delete(path);
        }
    }

    private static bool HasPlaintextSqliteHeader(string databasePath)
    {
        Span<byte> header = stackalloc byte[16];
        using var file = File.OpenRead(databasePath);
        return file.Read(header) == header.Length && header.SequenceEqual("SQLite format 3\0"u8);
    }
}

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
            Assert.Empty(FindPlaintextConversionArtifacts(databasePath));
        }
        finally
        {
            DeleteDatabaseFiles(databasePath);
            DeletePlaintextConversionArtifacts(databasePath);
        }
    }

    [Fact]
    public async Task EnsureEncryptedAsync_RestoresPlaintextDatabase_WhenReplacementValidationFails()
    {
        var databasePath = Path.Combine(Path.GetTempPath(), $"syncfactors-encryption-rollback-{Guid.NewGuid():N}.db");
        const string password = "test-sqlcipher-rollback-password";

        try
        {
            SetSqlitePassword(null);
            await new SqliteDatabaseInitializer(new SqlitePathResolver(databasePath)).InitializeAsync(CancellationToken.None);
            var originalSchemaVersionCount = await OpenAndReadSchemaVersionAsync(databasePath, password: null);

            var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                SqliteDatabaseEncryptionMigrator.EnsureEncryptedAsync(
                    databasePath,
                    password,
                    (_, _, _) => Task.FromResult(false),
                    CancellationToken.None));

            Assert.Contains("could not be opened", exception.Message, StringComparison.Ordinal);
            Assert.True(HasPlaintextSqliteHeader(databasePath));
            Assert.Equal(
                originalSchemaVersionCount,
                await OpenAndReadSchemaVersionAsync(databasePath, password: null));
            Assert.Empty(FindPlaintextConversionArtifacts(databasePath));
            Assert.Empty(FindEncryptedConversionArtifacts(databasePath));
        }
        finally
        {
            DeleteDatabaseFiles(databasePath);
            DeletePlaintextConversionArtifacts(databasePath);
            DeleteEncryptedConversionArtifacts(databasePath);
        }
    }

    [Fact]
    public async Task EnsureEncryptedAsync_SerializesConcurrentConversionAndRechecksEncryptedState()
    {
        var databasePath = Path.Combine(Path.GetTempPath(), $"syncfactors-encryption-lock-{Guid.NewGuid():N}.db");
        const string password = "test-sqlcipher-lock-password";
        var validationEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseValidation = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var validationCalls = 0;

        try
        {
            SetSqlitePassword(null);
            await new SqliteDatabaseInitializer(new SqlitePathResolver(databasePath)).InitializeAsync(CancellationToken.None);

            var firstConversion = SqliteDatabaseEncryptionMigrator.EnsureEncryptedAsync(
                databasePath,
                password,
                async (_, _, cancellationToken) =>
                {
                    Interlocked.Increment(ref validationCalls);
                    validationEntered.TrySetResult();
                    await releaseValidation.Task.WaitAsync(cancellationToken);
                    return true;
                },
                CancellationToken.None);
            await validationEntered.Task.WaitAsync(TimeSpan.FromSeconds(10));

            var secondConversion = SqliteDatabaseEncryptionMigrator.EnsureEncryptedAsync(
                databasePath,
                password,
                CancellationToken.None);
            await Task.Delay(TimeSpan.FromMilliseconds(250));
            Assert.False(secondConversion.IsCompleted);

            releaseValidation.TrySetResult();
            await Task.WhenAll(firstConversion, secondConversion);

            Assert.Equal(1, validationCalls);
            Assert.False(HasPlaintextSqliteHeader(databasePath));
            Assert.True(await OpenAndReadSchemaVersionAsync(databasePath, password) > 0);
            Assert.Empty(FindPlaintextConversionArtifacts(databasePath));
            Assert.True(File.Exists($"{databasePath}.encryption.lock"));
        }
        finally
        {
            releaseValidation.TrySetResult();
            DeleteDatabaseFiles(databasePath);
            DeletePlaintextConversionArtifacts(databasePath);
            DeleteEncryptedConversionArtifacts(databasePath);
        }
    }

    [Fact]
    public async Task EnsureEncryptedAsync_RecoversMissingLiveDatabaseFromValidatedInterruptedArtifacts()
    {
        var databasePath = Path.Combine(Path.GetTempPath(), $"syncfactors-encryption-interrupted-{Guid.NewGuid():N}.db");
        var encryptedSourcePath = Path.Combine(Path.GetTempPath(), $"syncfactors-encryption-source-{Guid.NewGuid():N}.db");
        var backupPath = $"{databasePath}.plaintext-20260731010101.bak";
        var encryptedTemporaryPath = $"{databasePath}.encrypted-{Guid.NewGuid():N}.tmp";
        const string password = "test-sqlcipher-interrupted-password";
        const string marker = "preserve-this-production-state";

        try
        {
            SetSqlitePassword(null);
            await new SqliteDatabaseInitializer(new SqlitePathResolver(databasePath)).InitializeAsync(CancellationToken.None);
            await WriteRecoveryMarkerAsync(databasePath, password: null, marker);

            File.Copy(databasePath, encryptedSourcePath);
            await SqliteDatabaseEncryptionMigrator.EnsureEncryptedAsync(
                encryptedSourcePath,
                password,
                CancellationToken.None);
            File.Copy(databasePath, backupPath);
            File.Move(encryptedSourcePath, encryptedTemporaryPath);
            DeleteDatabaseMainAndSidecars(databasePath);

            await SqliteDatabaseEncryptionMigrator.EnsureEncryptedAsync(
                databasePath,
                password,
                CancellationToken.None);

            Assert.False(HasPlaintextSqliteHeader(databasePath));
            Assert.Equal(marker, await ReadRecoveryMarkerAsync(databasePath, password));
            Assert.Empty(FindPlaintextConversionArtifacts(databasePath));
            Assert.Empty(FindEncryptedConversionArtifacts(databasePath));
        }
        finally
        {
            DeleteDatabaseFiles(databasePath);
            DeleteDatabaseFiles(encryptedSourcePath);
            DeletePlaintextConversionArtifacts(databasePath);
            DeleteEncryptedConversionArtifacts(databasePath);
        }
    }

    [Fact]
    public async Task EnsureEncryptedAsync_CleansValidatedPlaintextBackupAfterInterruptedActivation()
    {
        var databasePath = Path.Combine(Path.GetTempPath(), $"syncfactors-encryption-activated-{Guid.NewGuid():N}.db");
        var encryptedSourcePath = Path.Combine(Path.GetTempPath(), $"syncfactors-encryption-source-{Guid.NewGuid():N}.db");
        var backupPath = $"{databasePath}.plaintext-20260731020202.bak";
        const string password = "test-sqlcipher-activated-password";
        const string marker = "encrypted-live-remains-authoritative";

        try
        {
            SetSqlitePassword(null);
            await new SqliteDatabaseInitializer(new SqlitePathResolver(databasePath)).InitializeAsync(CancellationToken.None);
            await WriteRecoveryMarkerAsync(databasePath, password: null, marker);
            File.Copy(databasePath, backupPath);
            File.Copy(databasePath, encryptedSourcePath);
            await SqliteDatabaseEncryptionMigrator.EnsureEncryptedAsync(
                encryptedSourcePath,
                password,
                CancellationToken.None);
            DeleteDatabaseMainAndSidecars(databasePath);
            File.Move(encryptedSourcePath, databasePath);

            await SqliteDatabaseEncryptionMigrator.EnsureEncryptedAsync(
                databasePath,
                password,
                CancellationToken.None);

            Assert.False(HasPlaintextSqliteHeader(databasePath));
            Assert.Equal(marker, await ReadRecoveryMarkerAsync(databasePath, password));
            Assert.False(File.Exists(backupPath));
            Assert.Empty(FindPlaintextConversionArtifacts(databasePath));
        }
        finally
        {
            DeleteDatabaseFiles(databasePath);
            DeleteDatabaseFiles(encryptedSourcePath);
            DeletePlaintextConversionArtifacts(databasePath);
            DeleteEncryptedConversionArtifacts(databasePath);
        }
    }

    [Fact]
    public async Task EnsureEncryptedAsync_ResumesPlaintextBackupCleanupWhenLiveIsIdenticalRecoveryCopy()
    {
        var databasePath = Path.Combine(Path.GetTempPath(), $"syncfactors-encryption-restored-copy-{Guid.NewGuid():N}.db");
        var backupPath = $"{databasePath}.plaintext-20260731021212.bak";
        const string password = "test-sqlcipher-restored-copy-password";
        const string marker = "identical-restored-copy";

        try
        {
            SetSqlitePassword(null);
            await new SqliteDatabaseInitializer(new SqlitePathResolver(databasePath)).InitializeAsync(CancellationToken.None);
            await WriteRecoveryMarkerAsync(databasePath, password: null, marker);
            File.Copy(databasePath, backupPath);

            await SqliteDatabaseEncryptionMigrator.EnsureEncryptedAsync(
                databasePath,
                password,
                CancellationToken.None);

            Assert.False(HasPlaintextSqliteHeader(databasePath));
            Assert.Equal(marker, await ReadRecoveryMarkerAsync(databasePath, password));
            Assert.False(File.Exists(backupPath));
            Assert.Empty(FindPlaintextConversionArtifacts(databasePath));
        }
        finally
        {
            DeleteDatabaseFiles(databasePath);
            DeletePlaintextConversionArtifacts(databasePath);
            DeleteEncryptedConversionArtifacts(databasePath);
        }
    }

    [Fact]
    public async Task EnsureEncryptedAsync_FailsClosedWhenLivePlaintextAndBackupDiffer()
    {
        var databasePath = Path.Combine(Path.GetTempPath(), $"syncfactors-encryption-divergent-copy-{Guid.NewGuid():N}.db");
        var backupPath = $"{databasePath}.plaintext-20260731022222.bak";
        const string password = "test-sqlcipher-divergent-copy-password";

        try
        {
            SetSqlitePassword(null);
            await new SqliteDatabaseInitializer(new SqlitePathResolver(databasePath)).InitializeAsync(CancellationToken.None);
            await WriteRecoveryMarkerAsync(databasePath, password: null, "backup-state");
            File.Copy(databasePath, backupPath);
            await WriteRecoveryMarkerAsync(databasePath, password: null, "newer-live-state");
            var backupBytes = await File.ReadAllBytesAsync(backupPath);

            var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                SqliteDatabaseEncryptionMigrator.EnsureEncryptedAsync(
                    databasePath,
                    password,
                    CancellationToken.None));

            Assert.Contains("ambiguous", exception.Message, StringComparison.OrdinalIgnoreCase);
            Assert.True(HasPlaintextSqliteHeader(databasePath));
            Assert.Equal("newer-live-state", await ReadRecoveryMarkerAsync(databasePath, password: null));
            Assert.Equal(backupBytes, await File.ReadAllBytesAsync(backupPath));
        }
        finally
        {
            DeleteDatabaseFiles(databasePath);
            DeletePlaintextConversionArtifacts(databasePath);
            DeleteEncryptedConversionArtifacts(databasePath);
        }
    }

    [Fact]
    public async Task EnsureEncryptedAsync_FailsClosedWhenInterruptedBackupsAreAmbiguous()
    {
        var databasePath = Path.Combine(Path.GetTempPath(), $"syncfactors-encryption-ambiguous-{Guid.NewGuid():N}.db");
        var firstBackupPath = $"{databasePath}.plaintext-20260731030303.bak";
        var secondBackupPath = $"{databasePath}.plaintext-20260731040404.bak";
        const string password = "test-sqlcipher-ambiguous-password";

        try
        {
            SetSqlitePassword(null);
            await new SqliteDatabaseInitializer(new SqlitePathResolver(databasePath)).InitializeAsync(CancellationToken.None);
            await WriteRecoveryMarkerAsync(databasePath, password: null, "ambiguous-state");
            File.Copy(databasePath, firstBackupPath);
            File.Copy(databasePath, secondBackupPath);
            var firstBackupBytes = await File.ReadAllBytesAsync(firstBackupPath);
            var secondBackupBytes = await File.ReadAllBytesAsync(secondBackupPath);
            DeleteDatabaseMainAndSidecars(databasePath);

            var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                SqliteDatabaseEncryptionMigrator.EnsureEncryptedAsync(
                    databasePath,
                    password,
                    CancellationToken.None));

            Assert.Contains("ambiguous", exception.Message, StringComparison.OrdinalIgnoreCase);
            Assert.False(File.Exists(databasePath));
            Assert.Equal(firstBackupBytes, await File.ReadAllBytesAsync(firstBackupPath));
            Assert.Equal(secondBackupBytes, await File.ReadAllBytesAsync(secondBackupPath));
        }
        finally
        {
            DeleteDatabaseFiles(databasePath);
            DeletePlaintextConversionArtifacts(databasePath);
            DeleteEncryptedConversionArtifacts(databasePath);
        }
    }

    [Fact]
    public async Task EnsureEncryptedAsync_FailsClosedWhenInterruptedArtifactIsInvalid()
    {
        var databasePath = Path.Combine(Path.GetTempPath(), $"syncfactors-encryption-invalid-{Guid.NewGuid():N}.db");
        var backupPath = $"{databasePath}.plaintext-20260731050505.bak";
        var encryptedTemporaryPath = $"{databasePath}.encrypted-{Guid.NewGuid():N}.tmp";
        const string password = "test-sqlcipher-invalid-password";

        try
        {
            SetSqlitePassword(null);
            await new SqliteDatabaseInitializer(new SqlitePathResolver(databasePath)).InitializeAsync(CancellationToken.None);
            await WriteRecoveryMarkerAsync(databasePath, password: null, "valid-backup");
            File.Copy(databasePath, backupPath);
            await File.WriteAllBytesAsync(encryptedTemporaryPath, "not-a-valid-sqlcipher-database"u8.ToArray());
            var backupBytes = await File.ReadAllBytesAsync(backupPath);
            var temporaryBytes = await File.ReadAllBytesAsync(encryptedTemporaryPath);
            DeleteDatabaseMainAndSidecars(databasePath);

            var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                SqliteDatabaseEncryptionMigrator.EnsureEncryptedAsync(
                    databasePath,
                    password,
                    CancellationToken.None));

            Assert.Contains("invalid", exception.Message, StringComparison.OrdinalIgnoreCase);
            Assert.False(File.Exists(databasePath));
            Assert.Equal(backupBytes, await File.ReadAllBytesAsync(backupPath));
            Assert.Equal(temporaryBytes, await File.ReadAllBytesAsync(encryptedTemporaryPath));
        }
        finally
        {
            DeleteDatabaseFiles(databasePath);
            DeletePlaintextConversionArtifacts(databasePath);
            DeleteEncryptedConversionArtifacts(databasePath);
        }
    }

    [Fact]
    public async Task InitializeAsync_ReopensEncryptedDatabase_WhenSamePasswordIsConfigured()
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
    public async Task InitializeAsync_ThrowsWhenEncryptedDatabaseUsesDifferentPassword()
    {
        var databasePath = Path.Combine(Path.GetTempPath(), $"syncfactors-encrypted-wrong-password-{Guid.NewGuid():N}.db");

        try
        {
            SetSqlitePassword("test-sqlcipher-original-password");
            await new SqliteDatabaseInitializer(new SqlitePathResolver(databasePath)).InitializeAsync(CancellationToken.None);

            SetSqlitePassword("test-sqlcipher-different-password");
            var ex = await Assert.ThrowsAsync<InvalidOperationException>(
                () => new SqliteDatabaseInitializer(new SqlitePathResolver(databasePath)).InitializeAsync(CancellationToken.None));

            Assert.Contains("configured SQLCipher password", ex.Message, StringComparison.Ordinal);
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

    private static async Task WriteRecoveryMarkerAsync(string databasePath, string? password, string marker)
    {
        await using var connection = OpenTestConnection(databasePath, password, SqliteOpenMode.ReadWrite);
        await connection.OpenAsync();
        await using (var command = connection.CreateCommand())
        {
            command.CommandText =
                """
                CREATE TABLE IF NOT EXISTS encryption_recovery_probe (
                  marker TEXT NOT NULL
                );
                DELETE FROM encryption_recovery_probe;
                INSERT INTO encryption_recovery_probe (marker) VALUES ($marker);
                """;
            command.Parameters.AddWithValue("$marker", marker);
            await command.ExecuteNonQueryAsync();
        }

        await using var checkpoint = connection.CreateCommand();
        checkpoint.CommandText = "PRAGMA wal_checkpoint(TRUNCATE);";
        await checkpoint.ExecuteNonQueryAsync();
    }

    private static async Task<string?> ReadRecoveryMarkerAsync(string databasePath, string? password)
    {
        await using var connection = OpenTestConnection(databasePath, password, SqliteOpenMode.ReadOnly);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT marker FROM encryption_recovery_probe LIMIT 1;";
        return (string?)await command.ExecuteScalarAsync();
    }

    private static SqliteConnection OpenTestConnection(
        string databasePath,
        string? password,
        SqliteOpenMode mode)
    {
        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = mode,
            Pooling = false,
        };
        if (!string.IsNullOrWhiteSpace(password))
        {
            builder.Password = password;
        }

        return new SqliteConnection(builder.ToString());
    }

    private static void DeleteDatabaseFiles(string databasePath)
    {
        DeleteIfExists(databasePath);
        DeleteIfExists($"{databasePath}-wal");
        DeleteIfExists($"{databasePath}-shm");
        DeleteIfExists($"{databasePath}.encryption.lock");
    }

    private static void DeleteDatabaseMainAndSidecars(string databasePath)
    {
        DeleteIfExists($"{databasePath}-wal");
        DeleteIfExists($"{databasePath}-shm");
        DeleteIfExists($"{databasePath}-journal");
        DeleteIfExists(databasePath);
    }

    private static string[] FindPlaintextConversionArtifacts(string databasePath) =>
        Directory.GetFiles(
            Path.GetDirectoryName(databasePath)!,
            $"{Path.GetFileName(databasePath)}.plaintext-*.bak*");

    private static string[] FindEncryptedConversionArtifacts(string databasePath) =>
        Directory.GetFiles(
            Path.GetDirectoryName(databasePath)!,
            $"{Path.GetFileName(databasePath)}.encrypted-*.tmp*");

    private static void DeletePlaintextConversionArtifacts(string databasePath)
    {
        foreach (var path in FindPlaintextConversionArtifacts(databasePath))
        {
            File.Delete(path);
        }
    }

    private static void DeleteEncryptedConversionArtifacts(string databasePath)
    {
        foreach (var path in FindEncryptedConversionArtifacts(databasePath))
        {
            File.Delete(path);
        }
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

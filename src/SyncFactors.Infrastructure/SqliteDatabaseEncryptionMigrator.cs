using Microsoft.Data.Sqlite;

namespace SyncFactors.Infrastructure;

internal static class SqliteDatabaseEncryptionMigrator
{
    public static void RecoverInterruptedConversionIfNeeded(string databasePath, string? password)
    {
        if (File.Exists(databasePath))
        {
            return;
        }

        var directoryPath = Path.GetDirectoryName(databasePath);
        if (string.IsNullOrWhiteSpace(directoryPath) || !Directory.Exists(directoryPath))
        {
            return;
        }

        var databaseFileName = Path.GetFileName(databasePath);
        var interruptedOutputPaths = Directory
            .EnumerateFiles(directoryPath, $"{databaseFileName}.encrypted-*.tmp")
            .ToArray();
        if (interruptedOutputPaths.Length == 0)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(password))
        {
            throw new InvalidOperationException(
                "SQLCipher conversion recovery requires the configured SQLCipher password before opening the database.");
        }

        var backupPath = Directory
            .EnumerateFiles(directoryPath, $"{databaseFileName}.plaintext-*.bak")
            .OrderByDescending(File.GetLastWriteTimeUtc)
            .FirstOrDefault();
        if (backupPath is null)
        {
            throw new InvalidOperationException(
                "SQLCipher conversion was interrupted and no plaintext backup is available for recovery.");
        }

        try
        {
            RestorePlaintextDatabase(backupPath, databasePath);
            foreach (var interruptedOutputPath in interruptedOutputPaths)
            {
                File.Delete(interruptedOutputPath);
            }
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                "SQLCipher conversion recovery could not restore the plaintext database before startup.",
                ex);
        }
    }

    public static async Task EnsureEncryptedAsync(
        string databasePath,
        string password,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(password))
        {
            return;
        }

        RecoverInterruptedConversionIfNeeded(databasePath, password);
        if (!File.Exists(databasePath)
            || new FileInfo(databasePath).Length == 0)
        {
            return;
        }

        if (HasPlaintextSqliteHeader(databasePath))
        {
            if (!await CanOpenPlaintextAsync(databasePath, cancellationToken))
            {
                throw new InvalidOperationException("Plaintext SQLite database could not be opened before SQLCipher conversion.");
            }

            await ConvertPlaintextDatabaseAsync(databasePath, password, cancellationToken);
            return;
        }

        if (await CanOpenAsync(databasePath, password, cancellationToken))
        {
            return;
        }

        if (!await CanOpenPlaintextAsync(databasePath, cancellationToken))
        {
            throw new InvalidOperationException(
                "SQLite database could not be opened with the configured SQLCipher password or as plaintext. Verify the configured key.");
        }

        await ConvertPlaintextDatabaseAsync(databasePath, password, cancellationToken);
    }

    private static async Task<bool> CanOpenAsync(string databasePath, string password, CancellationToken cancellationToken)
    {
        try
        {
            await using var connection = Open(databasePath, password);
            await connection.OpenAsync(cancellationToken);
            await CountSqliteObjectsAsync(connection, cancellationToken);
            return true;
        }
        catch (SqliteException)
        {
            return false;
        }
    }

    private static bool HasPlaintextSqliteHeader(string databasePath)
    {
        Span<byte> header = stackalloc byte[16];
        using var file = File.OpenRead(databasePath);
        if (file.Read(header) != header.Length)
        {
            return false;
        }

        return header.SequenceEqual("SQLite format 3\0"u8);
    }

    private static async Task<bool> CanOpenPlaintextAsync(string databasePath, CancellationToken cancellationToken)
    {
        try
        {
            await using var connection = SqliteConnections.OpenPlaintext(databasePath);
            await connection.OpenAsync(cancellationToken);
            await CountSqliteObjectsAsync(connection, cancellationToken);
            return true;
        }
        catch (SqliteException)
        {
            return false;
        }
    }

    private static async Task ConvertPlaintextDatabaseAsync(
        string databasePath,
        string password,
        CancellationToken cancellationToken)
    {
        var encryptedPath = $"{databasePath}.encrypted-{Guid.NewGuid():N}.tmp";
        var backupSuffix = DateTimeOffset.UtcNow.ToString("yyyyMMddHHmmss");
        var backupPath = $"{databasePath}.plaintext-{backupSuffix}.bak";
        var plaintextSourceMoved = false;
        var encryptedOutputPromoted = false;

        try
        {
            await using (var connection = SqliteConnections.OpenPlaintext(databasePath))
            {
                await connection.OpenAsync(cancellationToken);
                await TruncateWalAsync(connection, cancellationToken);

                await using (var attach = connection.CreateCommand())
                {
                    attach.CommandText = "ATTACH DATABASE $encryptedPath AS encrypted KEY $password;";
                    attach.Parameters.AddWithValue("$encryptedPath", encryptedPath);
                    attach.Parameters.AddWithValue("$password", password);
                    await attach.ExecuteNonQueryAsync(cancellationToken);
                }

                await ExportEncryptedDatabaseAsync(connection, cancellationToken);
                await DetachEncryptedDatabaseAsync(connection, cancellationToken);
            }

            File.Move(databasePath, backupPath);
            plaintextSourceMoved = true;
            MoveIfExists($"{databasePath}-wal", $"{backupPath}-wal");
            MoveIfExists($"{databasePath}-shm", $"{backupPath}-shm");
            File.Move(encryptedPath, databasePath);
            encryptedOutputPromoted = true;

            RuntimeFileSecurity.HardenFile(backupPath);
            RuntimeFileSecurity.HardenFile($"{backupPath}-wal");
            RuntimeFileSecurity.HardenFile($"{backupPath}-shm");
            RuntimeFileSecurity.HardenSqliteFiles(databasePath);
        }
        catch (Exception conversionException)
        {
            if (plaintextSourceMoved && !encryptedOutputPromoted)
            {
                try
                {
                    RestorePlaintextDatabase(backupPath, databasePath);
                }
                catch (Exception recoveryException)
                {
                    throw new InvalidOperationException(
                        "SQLCipher conversion failed and the plaintext database could not be restored.",
                        recoveryException);
                }
            }

            if (File.Exists(encryptedPath))
            {
                File.Delete(encryptedPath);
            }

            throw new InvalidOperationException("SQLCipher conversion failed.", conversionException);
        }
    }

    private static void RestorePlaintextDatabase(string backupPath, string databasePath)
    {
        File.Move(backupPath, databasePath);
        MoveIfExists($"{backupPath}-wal", $"{databasePath}-wal");
        MoveIfExists($"{backupPath}-shm", $"{databasePath}-shm");
    }

    private static SqliteConnection Open(string databasePath, string password)
    {
        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Password = password,
            Pooling = false,
        };

        return new SqliteConnection(builder.ToString());
    }

    private static async Task<object?> CountSqliteObjectsAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM sqlite_master;";
        return await command.ExecuteScalarAsync(cancellationToken);
    }

    private static async Task TruncateWalAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA wal_checkpoint(TRUNCATE);";
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task ExportEncryptedDatabaseAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT sqlcipher_export('encrypted');";
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task DetachEncryptedDatabaseAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "DETACH DATABASE encrypted;";
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static void MoveIfExists(string sourcePath, string destinationPath)
    {
        if (File.Exists(sourcePath))
        {
            File.Move(sourcePath, destinationPath);
        }
    }
}

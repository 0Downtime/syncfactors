using Microsoft.Data.Sqlite;

namespace SyncFactors.Infrastructure;

internal static class SqliteDatabaseEncryptionMigrator
{
    public static async Task EnsureEncryptedAsync(
        string databasePath,
        string password,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(password)
            || !File.Exists(databasePath)
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
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT COUNT(*) FROM sqlite_master;";
            await command.ExecuteScalarAsync(cancellationToken);
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
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT COUNT(*) FROM sqlite_master;";
            await command.ExecuteScalarAsync(cancellationToken);
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

        try
        {
            await using (var connection = SqliteConnections.OpenPlaintext(databasePath))
            {
                await connection.OpenAsync(cancellationToken);
                await using (var checkpoint = connection.CreateCommand())
                {
                    checkpoint.CommandText = "PRAGMA wal_checkpoint(TRUNCATE);";
                    await checkpoint.ExecuteNonQueryAsync(cancellationToken);
                }

                await using (var attach = connection.CreateCommand())
                {
                    attach.CommandText = "ATTACH DATABASE $encryptedPath AS encrypted KEY $password;";
                    attach.Parameters.AddWithValue("$encryptedPath", encryptedPath);
                    attach.Parameters.AddWithValue("$password", password);
                    await attach.ExecuteNonQueryAsync(cancellationToken);
                }

                await using (var export = connection.CreateCommand())
                {
                    export.CommandText = "SELECT sqlcipher_export('encrypted');";
                    await export.ExecuteNonQueryAsync(cancellationToken);
                }

                await using (var detach = connection.CreateCommand())
                {
                    detach.CommandText = "DETACH DATABASE encrypted;";
                    await detach.ExecuteNonQueryAsync(cancellationToken);
                }
            }

            File.Move(databasePath, backupPath);
            MoveIfExists($"{databasePath}-wal", $"{backupPath}-wal");
            MoveIfExists($"{databasePath}-shm", $"{backupPath}-shm");
            File.Move(encryptedPath, databasePath);

            RuntimeFileSecurity.HardenFile(backupPath);
            RuntimeFileSecurity.HardenFile($"{backupPath}-wal");
            RuntimeFileSecurity.HardenFile($"{backupPath}-shm");
            RuntimeFileSecurity.HardenSqliteFiles(databasePath);
        }
        catch
        {
            if (File.Exists(encryptedPath))
            {
                File.Delete(encryptedPath);
            }

            throw;
        }
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

    private static void MoveIfExists(string sourcePath, string destinationPath)
    {
        if (File.Exists(sourcePath))
        {
            File.Move(sourcePath, destinationPath);
        }
    }
}

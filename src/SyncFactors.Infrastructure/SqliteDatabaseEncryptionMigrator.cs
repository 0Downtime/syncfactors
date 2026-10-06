using Microsoft.Data.Sqlite;

namespace SyncFactors.Infrastructure;

internal static class SqliteDatabaseEncryptionMigrator
{
    private static readonly TimeSpan EncryptionLockTimeout = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan EncryptionLockRetryDelay = TimeSpan.FromMilliseconds(100);

    public static async Task EnsureEncryptedAsync(
        string databasePath,
        string password,
        CancellationToken cancellationToken)
    {
        await EnsureEncryptedAsync(databasePath, password, CanOpenAsync, cancellationToken);
    }

    internal static async Task EnsureEncryptedAsync(
        string databasePath,
        string password,
        Func<string, string, CancellationToken, Task<bool>> encryptedDatabaseValidator,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(encryptedDatabaseValidator);

        if (string.IsNullOrWhiteSpace(password))
        {
            return;
        }

        await using var encryptionLock = await AcquireEncryptionLockAsync(databasePath, cancellationToken);

        var reconciliationResult = await ReconcileInterruptedConversionAsync(
            databasePath,
            password,
            encryptedDatabaseValidator,
            cancellationToken);
        if (reconciliationResult == ReconciliationResult.EncryptedDatabaseReady)
        {
            return;
        }

        // Another API/worker process may have completed conversion while this
        // process waited for the cross-process lock. Recheck all state only
        // after exclusive ownership has been acquired.
        if (!File.Exists(databasePath) || new FileInfo(databasePath).Length == 0)
        {
            return;
        }

        if (HasPlaintextSqliteHeader(databasePath))
        {
            if (!await CanOpenPlaintextAsync(databasePath, cancellationToken))
            {
                throw new InvalidOperationException("Plaintext SQLite database could not be opened before SQLCipher conversion.");
            }

            await ConvertPlaintextDatabaseAsync(
                databasePath,
                password,
                encryptedDatabaseValidator,
                cancellationToken);
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

        await ConvertPlaintextDatabaseAsync(
            databasePath,
            password,
            encryptedDatabaseValidator,
            cancellationToken);
    }

    private static async Task<ReconciliationResult> ReconcileInterruptedConversionAsync(
        string databasePath,
        string password,
        Func<string, string, CancellationToken, Task<bool>> encryptedDatabaseValidator,
        CancellationToken cancellationToken)
    {
        var artifacts = DiscoverConversionArtifacts(databasePath);
        if (artifacts.PlaintextBackup is null && artifacts.EncryptedTemporaryDatabase is null)
        {
            return ReconciliationResult.None;
        }

        var liveDatabaseExists = File.Exists(databasePath) && new FileInfo(databasePath).Length > 0;
        if (liveDatabaseExists && HasPlaintextSqliteHeader(databasePath))
        {
            if (artifacts.PlaintextBackup is not null)
            {
                if (artifacts.EncryptedTemporaryDatabase is not null ||
                    !await ArtifactMatchesLiveDatabaseAsync(
                        artifacts.PlaintextBackup,
                        databasePath,
                        cancellationToken) ||
                    !await CanOpenPlaintextAsync(databasePath, cancellationToken) ||
                    !await CanOpenPlaintextAsync(artifacts.PlaintextBackup.MainPath, cancellationToken))
                {
                    throw CreateAmbiguousArtifactsException(
                        "both a live plaintext database and a different or unverified plaintext conversion backup are present");
                }

                // Recovery copies the validated backup before deleting it. If the
                // process stopped during backup cleanup, byte identity proves the
                // live database is that completed copy and cleanup can resume.
                DeleteArtifactGroup(artifacts.PlaintextBackup);
                return ReconciliationResult.None;
            }

            if (artifacts.EncryptedTemporaryDatabase is not null)
            {
                await RequireValidEncryptedArtifactAsync(
                    artifacts.EncryptedTemporaryDatabase,
                    password,
                    encryptedDatabaseValidator,
                    cancellationToken);
                DeleteArtifactGroup(artifacts.EncryptedTemporaryDatabase);
            }

            return ReconciliationResult.None;
        }

        if (liveDatabaseExists && await CanOpenAsync(databasePath, password, cancellationToken))
        {
            await RequireValidArtifactsAsync(
                artifacts,
                password,
                encryptedDatabaseValidator,
                cancellationToken);
            DeleteConversionArtifacts(artifacts);
            RuntimeFileSecurity.HardenSqliteFiles(databasePath);
            return ReconciliationResult.EncryptedDatabaseReady;
        }

        await RequireValidArtifactsAsync(
            artifacts,
            password,
            encryptedDatabaseValidator,
            cancellationToken);

        var recoverySource = artifacts.EncryptedTemporaryDatabase ?? artifacts.PlaintextBackup;
        if (recoverySource is null)
        {
            return ReconciliationResult.None;
        }

        DeleteLiveDatabaseFiles(databasePath);
        try
        {
            CopyArtifactToLiveDatabase(recoverySource, databasePath);
            var recoveredAsEncrypted = recoverySource == artifacts.EncryptedTemporaryDatabase;
            var recoveredDatabaseIsValid = recoveredAsEncrypted
                ? await CanOpenAsync(databasePath, password, cancellationToken) &&
                  await encryptedDatabaseValidator(databasePath, password, cancellationToken)
                : await CanOpenPlaintextAsync(databasePath, cancellationToken);
            if (!recoveredDatabaseIsValid)
            {
                throw new InvalidOperationException(
                    "Interrupted SQLCipher conversion artifacts could not reconstruct a valid live SQLite database.");
            }

            RuntimeFileSecurity.HardenSqliteFiles(databasePath);
            DeleteConversionArtifacts(artifacts);
            return recoveredAsEncrypted
                ? ReconciliationResult.EncryptedDatabaseReady
                : ReconciliationResult.PlaintextDatabaseRestored;
        }
        catch
        {
            // The validated source artifacts remain untouched until the live copy
            // is proven usable, so a failed reconstruction can be retried safely.
            TryDeleteLiveDatabaseFiles(databasePath);
            throw;
        }
    }

    private static ConversionArtifacts DiscoverConversionArtifacts(string databasePath)
    {
        var directory = Path.GetDirectoryName(databasePath);
        if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
        {
            return new ConversionArtifacts(null, null);
        }

        var databaseFileName = Path.GetFileName(databasePath);
        var files = Directory.GetFiles(directory);
        var plaintextPrefix = $"{databaseFileName}.plaintext-";
        var encryptedPrefix = $"{databaseFileName}.encrypted-";
        var plaintextFiles = files
            .Where(path => Path.GetFileName(path).StartsWith(plaintextPrefix, StringComparison.Ordinal))
            .OrderBy(path => path, StringComparer.Ordinal)
            .ToArray();
        var encryptedFiles = files
            .Where(path => Path.GetFileName(path).StartsWith(encryptedPrefix, StringComparison.Ordinal))
            .OrderBy(path => path, StringComparer.Ordinal)
            .ToArray();

        return new ConversionArtifacts(
            BuildArtifactGroup("plaintext backup", plaintextFiles, ".bak"),
            BuildArtifactGroup("encrypted temporary database", encryptedFiles, ".tmp"));
    }

    private static ConversionArtifactGroup? BuildArtifactGroup(
        string description,
        IReadOnlyList<string> artifactPaths,
        string mainFileSuffix)
    {
        if (artifactPaths.Count == 0)
        {
            return null;
        }

        var mainPaths = artifactPaths
            .Where(path => Path.GetFileName(path).EndsWith(mainFileSuffix, StringComparison.Ordinal))
            .ToArray();
        if (mainPaths.Length != 1)
        {
            throw CreateAmbiguousArtifactsException(
                $"expected exactly one {description}, but found {mainPaths.Length}");
        }

        var mainPath = mainPaths[0];
        if (artifactPaths.Any(path => !IsArtifactPath(mainPath, path)))
        {
            throw CreateAmbiguousArtifactsException(
                $"the {description} has unexpected or unrelated companion files");
        }

        return new ConversionArtifactGroup(mainPath);
    }

    private static bool IsArtifactPath(string mainPath, string candidatePath) =>
        string.Equals(mainPath, candidatePath, StringComparison.Ordinal) ||
        string.Equals($"{mainPath}-wal", candidatePath, StringComparison.Ordinal) ||
        string.Equals($"{mainPath}-shm", candidatePath, StringComparison.Ordinal) ||
        string.Equals($"{mainPath}-journal", candidatePath, StringComparison.Ordinal);

    private static async Task RequireValidArtifactsAsync(
        ConversionArtifacts artifacts,
        string password,
        Func<string, string, CancellationToken, Task<bool>> encryptedDatabaseValidator,
        CancellationToken cancellationToken)
    {
        if (artifacts.PlaintextBackup is not null &&
            !await CanOpenPlaintextAsync(artifacts.PlaintextBackup.MainPath, cancellationToken))
        {
            throw CreateInvalidArtifactException("plaintext conversion backup");
        }

        if (artifacts.EncryptedTemporaryDatabase is not null)
        {
            await RequireValidEncryptedArtifactAsync(
                artifacts.EncryptedTemporaryDatabase,
                password,
                encryptedDatabaseValidator,
                cancellationToken);
        }
    }

    private static async Task RequireValidEncryptedArtifactAsync(
        ConversionArtifactGroup artifact,
        string password,
        Func<string, string, CancellationToken, Task<bool>> encryptedDatabaseValidator,
        CancellationToken cancellationToken)
    {
        if (!await CanOpenAsync(artifact.MainPath, password, cancellationToken) ||
            !await encryptedDatabaseValidator(artifact.MainPath, password, cancellationToken))
        {
            throw CreateInvalidArtifactException("encrypted temporary database");
        }
    }

    private static InvalidOperationException CreateAmbiguousArtifactsException(string reason) =>
        new(
            $"Interrupted SQLCipher conversion artifacts are ambiguous ({reason}). " +
            "Startup stopped without changing the live database or conversion artifacts.");

    private static InvalidOperationException CreateInvalidArtifactException(string description) =>
        new(
            $"Interrupted SQLCipher conversion {description} is invalid. " +
            "Startup stopped without changing the live database or conversion artifacts.");

    private static void CopyArtifactToLiveDatabase(
        ConversionArtifactGroup artifact,
        string databasePath)
    {
        File.Copy(artifact.MainPath, databasePath, overwrite: false);
        CopyCompanionIfExists(artifact.MainPath, databasePath, "-wal");
        CopyCompanionIfExists(artifact.MainPath, databasePath, "-shm");
        CopyCompanionIfExists(artifact.MainPath, databasePath, "-journal");
    }

    private static void CopyCompanionIfExists(string sourceMainPath, string destinationMainPath, string suffix)
    {
        var sourcePath = $"{sourceMainPath}{suffix}";
        if (File.Exists(sourcePath))
        {
            File.Copy(sourcePath, $"{destinationMainPath}{suffix}", overwrite: false);
        }
    }

    private static async Task<bool> ArtifactMatchesLiveDatabaseAsync(
        ConversionArtifactGroup artifact,
        string databasePath,
        CancellationToken cancellationToken)
    {
        if (!await FilesMatchAsync(artifact.MainPath, databasePath, cancellationToken))
        {
            return false;
        }

        foreach (var suffix in new[] { "-wal", "-shm", "-journal" })
        {
            var artifactCompanionPath = $"{artifact.MainPath}{suffix}";
            if (!File.Exists(artifactCompanionPath))
            {
                continue;
            }

            var liveCompanionPath = $"{databasePath}{suffix}";
            if (!File.Exists(liveCompanionPath) ||
                !await FilesMatchAsync(artifactCompanionPath, liveCompanionPath, cancellationToken))
            {
                return false;
            }
        }

        return true;
    }

    private static async Task<bool> FilesMatchAsync(
        string firstPath,
        string secondPath,
        CancellationToken cancellationToken)
    {
        if (new FileInfo(firstPath).Length != new FileInfo(secondPath).Length)
        {
            return false;
        }

        const int bufferSize = 64 * 1024;
        await using var first = new FileStream(
            firstPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        await using var second = new FileStream(
            secondPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        var firstBuffer = new byte[bufferSize];
        var secondBuffer = new byte[bufferSize];
        while (true)
        {
            var firstRead = await first.ReadAsync(firstBuffer, cancellationToken);
            var secondRead = await second.ReadAsync(secondBuffer, cancellationToken);
            if (firstRead != secondRead)
            {
                return false;
            }

            if (firstRead == 0)
            {
                return true;
            }

            if (!firstBuffer.AsSpan(0, firstRead).SequenceEqual(secondBuffer.AsSpan(0, secondRead)))
            {
                return false;
            }
        }
    }

    private static void DeleteConversionArtifacts(ConversionArtifacts artifacts)
    {
        if (artifacts.EncryptedTemporaryDatabase is not null)
        {
            DeleteArtifactGroup(artifacts.EncryptedTemporaryDatabase);
        }

        if (artifacts.PlaintextBackup is not null)
        {
            DeleteArtifactGroup(artifacts.PlaintextBackup);
        }
    }

    private static void DeleteArtifactGroup(ConversionArtifactGroup artifact)
    {
        DeleteIfExists($"{artifact.MainPath}-wal");
        DeleteIfExists($"{artifact.MainPath}-shm");
        DeleteIfExists($"{artifact.MainPath}-journal");

        File.Delete(artifact.MainPath);
    }

    private static void DeleteLiveDatabaseFiles(string databasePath)
    {
        DeleteIfExists($"{databasePath}-wal");
        DeleteIfExists($"{databasePath}-shm");
        DeleteIfExists($"{databasePath}-journal");
        DeleteIfExists(databasePath);
    }

    private static void TryDeleteLiveDatabaseFiles(string databasePath)
    {
        try
        {
            DeleteLiveDatabaseFiles(databasePath);
        }
        catch
        {
            // A validated recovery source still exists. A later startup will
            // retry reconciliation or fail closed without discarding it.
        }
    }

    private static async Task<bool> CanOpenAsync(string databasePath, string password, CancellationToken cancellationToken)
    {
        try
        {
            await using var connection = Open(databasePath, password, SqliteOpenMode.ReadOnly);
            await connection.OpenAsync(cancellationToken);
            await CountSqliteObjectsAsync(connection, cancellationToken);
            return true;
        }
        catch (SqliteException)
        {
            return false;
        }
    }

    private static async Task<FileStream> AcquireEncryptionLockAsync(
        string databasePath,
        CancellationToken cancellationToken)
    {
        var lockPath = $"{databasePath}.encryption.lock";
        var deadline = DateTimeOffset.UtcNow.Add(EncryptionLockTimeout);
        IOException? lastFailure = null;

        while (DateTimeOffset.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var stream = new FileStream(
                    lockPath,
                    FileMode.OpenOrCreate,
                    FileAccess.ReadWrite,
                    FileShare.None,
                    bufferSize: 1,
                    FileOptions.Asynchronous);
                RuntimeFileSecurity.HardenFile(lockPath);
                return stream;
            }
            catch (IOException ex)
            {
                lastFailure = ex;
                await Task.Delay(EncryptionLockRetryDelay, cancellationToken);
            }
        }

        throw new TimeoutException(
            "Timed out waiting for exclusive access to the SQLite encryption conversion lock.",
            lastFailure);
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
            await using var connection = SqliteConnections.OpenPlaintext(databasePath, SqliteOpenMode.ReadOnly);
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
        Func<string, string, CancellationToken, Task<bool>> encryptedDatabaseValidator,
        CancellationToken cancellationToken)
    {
        var encryptedPath = $"{databasePath}.encrypted-{Guid.NewGuid():N}.tmp";
        var backupSuffix = DateTimeOffset.UtcNow.ToString("yyyyMMddHHmmss");
        var backupPath = $"{databasePath}.plaintext-{backupSuffix}.bak";
        var databaseWalPath = $"{databasePath}-wal";
        var databaseShmPath = $"{databasePath}-shm";
        var backupWalPath = $"{backupPath}-wal";
        var backupShmPath = $"{backupPath}-shm";
        var originalDatabaseMoved = false;
        var originalWalMoved = false;
        var originalShmMoved = false;
        var encryptedDatabaseActivated = false;

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
            originalDatabaseMoved = true;
            originalWalMoved = MoveIfExists(databaseWalPath, backupWalPath);
            originalShmMoved = MoveIfExists(databaseShmPath, backupShmPath);
            File.Move(encryptedPath, databasePath);
            encryptedDatabaseActivated = true;

            RuntimeFileSecurity.HardenSqliteFiles(databasePath);

            if (!await encryptedDatabaseValidator(databasePath, password, cancellationToken))
            {
                throw new InvalidOperationException(
                    "SQLCipher conversion produced a database that could not be opened with the configured password.");
            }

            // Delete the plaintext main database last. If an earlier sidecar cleanup
            // fails, the catch block can still restore the checkpointed plaintext
            // database and leave startup recoverable.
            DeleteIfExists(backupWalPath);
            DeleteIfExists(backupShmPath);
            File.Delete(backupPath);
        }
        catch
        {
            if (originalDatabaseMoved)
            {
                if (encryptedDatabaseActivated)
                {
                    DeleteIfExists(databasePath);
                    DeleteIfExists(databaseWalPath);
                    DeleteIfExists(databaseShmPath);
                }

                if (File.Exists(backupPath))
                {
                    File.Move(backupPath, databasePath);
                }

                if (originalWalMoved && File.Exists(backupWalPath))
                {
                    File.Move(backupWalPath, databaseWalPath);
                }

                if (originalShmMoved && File.Exists(backupShmPath))
                {
                    File.Move(backupShmPath, databaseShmPath);
                }
            }

            DeleteIfExists(encryptedPath);
            throw;
        }
    }

    private static SqliteConnection Open(
        string databasePath,
        string password,
        SqliteOpenMode mode = SqliteOpenMode.ReadWriteCreate)
    {
        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = mode,
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

    private static bool MoveIfExists(string sourcePath, string destinationPath)
    {
        if (!File.Exists(sourcePath))
        {
            return false;
        }

        File.Move(sourcePath, destinationPath);
        return true;
    }

    private static void DeleteIfExists(string path)
    {
        if (File.Exists(path))
        {
            File.Delete(path);
        }
    }

    private enum ReconciliationResult
    {
        None,
        PlaintextDatabaseRestored,
        EncryptedDatabaseReady
    }

    private sealed record ConversionArtifacts(
        ConversionArtifactGroup? PlaintextBackup,
        ConversionArtifactGroup? EncryptedTemporaryDatabase);

    private sealed record ConversionArtifactGroup(string MainPath);
}

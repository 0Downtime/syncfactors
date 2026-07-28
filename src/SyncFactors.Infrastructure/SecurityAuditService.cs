using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using SyncFactors.Domain;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace SyncFactors.Infrastructure;

public interface ISecurityAuditService
{
    void Write(string eventType, string outcome, params (string Key, object? Value)[] fields);
}

public sealed class SecurityAuditService(ILogger<SecurityAuditService> logger) : ISecurityAuditService
{
    public const string IntegrityKeyEnvironmentVariable = "SYNCFACTORS_SECURITY_AUDIT_INTEGRITY_KEY";
    private const string AuditPathEnvironmentVariable = "SYNCFACTORS_SECURITY_AUDIT_LOG_PATH";
    private const string Sha256Algorithm = "SHA256";
    private const string HmacSha256Algorithm = "HMACSHA256";
    private const string MigrationKey = "legacy-jsonl-migration-v1";

    public void Write(string eventType, string outcome, params (string Key, object? Value)[] fields)
    {
        var values = fields
            .Where(field => field.Value is not null)
            .ToDictionary(field => field.Key, field => field.Value, StringComparer.OrdinalIgnoreCase);
        var logValues = values.ToDictionary(
            field => field.Key,
            field => LogSafety.RedactStructuredValue(field.Value),
            StringComparer.OrdinalIgnoreCase);

        logger.LogInformation(
            "SecurityAudit EventType={EventType} Outcome={Outcome} Fields={Fields}",
            eventType,
            outcome,
            logValues);

        var databasePath = ResolveAuditPath();
        EnsureDatabaseInitialized(databasePath);
        MigrateLegacyAuditIfNeeded(databasePath, ResolveLegacyAuditPath());
        AppendSqliteEntry(databasePath, eventType, outcome, values);
    }

    public static SecurityAuditIntegrityResult VerifyIntegrity(string path, bool requireKeyedIntegrity = false)
    {
        if (!File.Exists(path))
        {
            return new SecurityAuditIntegrityResult(false, 0, "Audit log was not found.");
        }

        return CanOpenAsSqliteDatabase(path)
            ? VerifySqliteIntegrity(path, requireKeyedIntegrity)
            : VerifyLegacyJsonlIntegrity(path, requireKeyedIntegrity);
    }

    public static IReadOnlyList<SecurityAuditEvent> ReadEventsSince(string path, DateTimeOffset sinceUtc)
    {
        var integrity = VerifyIntegrity(path);
        if (!integrity.IsValid)
        {
            throw new InvalidOperationException("Security audit integrity validation failed.");
        }

        if (!CanOpenAsSqliteDatabase(path))
        {
            return ReadLegacyEntries(path)
                .Where(entry => entry.TimestampUtc >= sinceUtc)
                .Select(entry => new SecurityAuditEvent(entry.TimestampUtc, entry.EventType))
                .ToArray();
        }

        using var connection = SqliteConnections.Open(path, SqliteOpenMode.ReadOnly);
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT timestamp_utc, event_type
            FROM security_audit_entries
            WHERE timestamp_utc >= $sinceUtc
            ORDER BY sequence;
            """;
        command.Parameters.AddWithValue("$sinceUtc", sinceUtc.ToString("O"));
        using var reader = command.ExecuteReader();
        var events = new List<SecurityAuditEvent>();
        while (reader.Read())
        {
            events.Add(new SecurityAuditEvent(
                DateTimeOffset.Parse(reader.GetString(0), System.Globalization.CultureInfo.InvariantCulture),
                reader.GetString(1)));
        }

        return events;
    }

    public static void ValidateStartup(bool isProduction)
    {
        if (isProduction && string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(IntegrityKeyEnvironmentVariable)))
        {
            throw new InvalidOperationException("Security audit integrity validation failed.");
        }

        var databasePath = ResolveAuditPath();
        var legacyPath = ResolveLegacyAuditPath();
        if (!File.Exists(databasePath) && !File.Exists(legacyPath))
        {
            return;
        }

        try
        {
            EnsureDatabaseInitialized(databasePath);
            MigrateLegacyAuditIfNeeded(databasePath, legacyPath);
            if (!VerifyIntegrity(databasePath, requireKeyedIntegrity: isProduction).IsValid)
            {
                throw new InvalidOperationException("Security audit integrity validation failed.");
            }
        }
        catch (Exception ex) when (ex is not InvalidOperationException ||
                                   !string.Equals(ex.Message, "Security audit integrity validation failed.", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Security audit integrity validation failed.");
        }
    }

    public static string ResolveAuditPath()
    {
        var configured = Environment.GetEnvironmentVariable(AuditPathEnvironmentVariable);
        if (!string.IsNullOrWhiteSpace(configured))
        {
            var fullPath = Path.GetFullPath(configured);
            return string.Equals(Path.GetExtension(fullPath), ".jsonl", StringComparison.OrdinalIgnoreCase)
                ? Path.ChangeExtension(fullPath, ".db")
                : fullPath;
        }

        return Path.Combine(ResolveRuntimeRoot(), "state", "runtime", "security-audit.db");
    }

    public static string ResolveLegacyAuditPath()
    {
        var configured = Environment.GetEnvironmentVariable(AuditPathEnvironmentVariable);
        if (!string.IsNullOrWhiteSpace(configured))
        {
            var fullPath = Path.GetFullPath(configured);
            return string.Equals(Path.GetExtension(fullPath), ".jsonl", StringComparison.OrdinalIgnoreCase)
                ? fullPath
                : Path.ChangeExtension(fullPath, ".jsonl");
        }

        return Path.Combine(ResolveRuntimeRoot(), "state", "runtime", "security-audit.jsonl");
    }

    private static string ResolveRuntimeRoot()
    {
        var repoRoot = Environment.GetEnvironmentVariable("REPO_ROOT");
        return string.IsNullOrWhiteSpace(repoRoot) ? Environment.CurrentDirectory : repoRoot;
    }

    private static void EnsureDatabaseInitialized(string databasePath)
    {
        RuntimeFileSecurity.EnsureParentDirectory(databasePath);
        using var connection = SqliteConnections.Open(databasePath);
        connection.Open();
        ConfigureAuditDurability(connection);
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            CREATE TABLE IF NOT EXISTS security_audit_entries (
                sequence INTEGER PRIMARY KEY AUTOINCREMENT,
                timestamp_utc TEXT NOT NULL,
                event_type TEXT NOT NULL,
                outcome TEXT NOT NULL,
                fields_json TEXT NOT NULL,
                algorithm TEXT NOT NULL,
                previous_hash TEXT NULL,
                entry_hash TEXT NOT NULL UNIQUE
            );
            CREATE TABLE IF NOT EXISTS security_audit_metadata (
                key TEXT PRIMARY KEY,
                value TEXT NOT NULL
            );
            """;
        command.ExecuteNonQuery();
        HardenAuditDatabaseFiles(databasePath);
    }

    private static void AppendSqliteEntry(
        string databasePath,
        string eventType,
        string outcome,
        IReadOnlyDictionary<string, object?> fields)
    {
        using var connection = SqliteConnections.Open(databasePath);
        connection.Open();
        ConfigureAuditDurability(connection);
        ExecuteImmediateTransaction(connection, () =>
        {
            var integrity = VerifySqliteEntries(connection, requireKeyedIntegrity: false);
            if (!integrity.IsValid)
            {
                throw new InvalidOperationException("Security audit integrity validation failed.");
            }

            var previousHash = ReadPreviousHash(connection);
            var timestampUtc = DateTimeOffset.UtcNow;
            var canonicalFields = CanonicalizeFields(fields);
            var algorithm = ResolveIntegrityAlgorithm();
            var entryHash = ComputeEntryHash(timestampUtc, eventType, outcome, canonicalFields, previousHash, algorithm);
            InsertEntry(connection, timestampUtc, eventType, outcome, canonicalFields, algorithm, previousHash, entryHash);
        });
        HardenAuditDatabaseFiles(databasePath);
    }

    private static void MigrateLegacyAuditIfNeeded(string databasePath, string legacyPath)
    {
        using var connection = SqliteConnections.Open(databasePath);
        connection.Open();
        ConfigureAuditDurability(connection);
        if (MigrationCompleted(connection))
        {
            return;
        }

        ExecuteImmediateTransaction(connection, () =>
        {
            if (MigrationCompleted(connection))
            {
                return;
            }

            IReadOnlyList<StoredAuditEntry> legacyEntries = [];
            if (File.Exists(legacyPath))
            {
                var verification = VerifyLegacyJsonlIntegrity(legacyPath, requireKeyedIntegrity: false);
                if (!verification.IsValid)
                {
                    throw new InvalidOperationException("Security audit integrity validation failed.");
                }

                legacyEntries = ReadLegacyEntries(legacyPath);
            }

            using (var countCommand = connection.CreateCommand())
            {
                countCommand.CommandText = "SELECT COUNT(*) FROM security_audit_entries;";
                var existingCount = Convert.ToInt64(countCommand.ExecuteScalar());
                if (existingCount != 0 && legacyEntries.Count != 0)
                {
                    throw new InvalidOperationException("Security audit integrity validation failed.");
                }
            }

            foreach (var entry in legacyEntries)
            {
                InsertEntry(
                    connection,
                    entry.TimestampUtc,
                    entry.EventType,
                    entry.Outcome,
                    entry.CanonicalFields,
                    entry.Algorithm,
                    entry.PreviousHash,
                    entry.EntryHash);
            }

            using var markerCommand = connection.CreateCommand();
            markerCommand.CommandText =
                "INSERT INTO security_audit_metadata(key, value) VALUES ($key, $value);";
            markerCommand.Parameters.AddWithValue("$key", MigrationKey);
            markerCommand.Parameters.AddWithValue("$value", DateTimeOffset.UtcNow.ToString("O"));
            markerCommand.ExecuteNonQuery();
        });
    }

    private static bool MigrationCompleted(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT 1 FROM security_audit_metadata WHERE key = $key LIMIT 1;";
        command.Parameters.AddWithValue("$key", MigrationKey);
        return command.ExecuteScalar() is not null;
    }

    private static void ExecuteImmediateTransaction(SqliteConnection connection, Action action)
    {
        using var begin = connection.CreateCommand();
        begin.CommandText = "BEGIN IMMEDIATE;";
        begin.ExecuteNonQuery();
        try
        {
            action();
            using var commit = connection.CreateCommand();
            commit.CommandText = "COMMIT;";
            commit.ExecuteNonQuery();
        }
        catch
        {
            try
            {
                using var rollback = connection.CreateCommand();
                rollback.CommandText = "ROLLBACK;";
                rollback.ExecuteNonQuery();
            }
            catch (SqliteException)
            {
            }

            throw;
        }
    }

    private static string? ReadPreviousHash(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT entry_hash FROM security_audit_entries ORDER BY sequence DESC LIMIT 1;";
        return command.ExecuteScalar() as string;
    }

    private static void InsertEntry(
        SqliteConnection connection,
        DateTimeOffset timestampUtc,
        string eventType,
        string outcome,
        string canonicalFields,
        string algorithm,
        string? previousHash,
        string entryHash)
    {
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT INTO security_audit_entries(
                timestamp_utc, event_type, outcome, fields_json, algorithm, previous_hash, entry_hash)
            VALUES ($timestampUtc, $eventType, $outcome, $fieldsJson, $algorithm, $previousHash, $entryHash);
            """;
        command.Parameters.AddWithValue("$timestampUtc", timestampUtc.ToString("O"));
        command.Parameters.AddWithValue("$eventType", eventType);
        command.Parameters.AddWithValue("$outcome", outcome);
        command.Parameters.AddWithValue("$fieldsJson", canonicalFields);
        command.Parameters.AddWithValue("$algorithm", algorithm);
        command.Parameters.AddWithValue("$previousHash", (object?)previousHash ?? DBNull.Value);
        command.Parameters.AddWithValue("$entryHash", entryHash);
        command.ExecuteNonQuery();
    }

    private static SecurityAuditIntegrityResult VerifySqliteIntegrity(string path, bool requireKeyedIntegrity)
    {
        try
        {
            using var connection = SqliteConnections.Open(path, SqliteOpenMode.ReadOnly);
            connection.Open();
            using (var integrityCommand = connection.CreateCommand())
            {
                integrityCommand.CommandText = "PRAGMA integrity_check;";
                if (!string.Equals(integrityCommand.ExecuteScalar() as string, "ok", StringComparison.OrdinalIgnoreCase))
                {
                    return IntegrityFailure(0, requireKeyedIntegrity, "Audit database integrity check failed.");
                }
            }

            return VerifySqliteEntries(connection, requireKeyedIntegrity);
        }
        catch (Exception ex) when (ex is SqliteException or IOException or JsonException or InvalidOperationException or FormatException)
        {
            return new SecurityAuditIntegrityResult(false, 0, "Audit log integrity verification failed.");
        }
    }

    private static SecurityAuditIntegrityResult VerifySqliteEntries(
        SqliteConnection connection,
        bool requireKeyedIntegrity)
    {
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT sequence, timestamp_utc, event_type, outcome, fields_json, algorithm, previous_hash, entry_hash
            FROM security_audit_entries
            ORDER BY sequence;
            """;
        using var reader = command.ExecuteReader();
        string? expectedPreviousHash = null;
        var entryCount = 0;
        long expectedSequence = 1;
        while (reader.Read())
        {
            entryCount++;
            if (reader.GetInt64(0) != expectedSequence++)
            {
                return IntegrityFailure(entryCount, requireKeyedIntegrity, "Audit entry sequence is not contiguous.");
            }

            var timestampUtc = DateTimeOffset.Parse(reader.GetString(1), System.Globalization.CultureInfo.InvariantCulture);
            var eventType = reader.GetString(2);
            var outcome = reader.GetString(3);
            var canonicalFields = CanonicalizeFields(JsonDocument.Parse(reader.GetString(4)).RootElement);
            var algorithm = reader.GetString(5);
            var previousHash = reader.IsDBNull(6) ? null : reader.GetString(6);
            var entryHash = reader.GetString(7);
            var failure = ValidateEntry(
                entryCount,
                requireKeyedIntegrity,
                timestampUtc,
                eventType,
                outcome,
                canonicalFields,
                algorithm,
                previousHash,
                entryHash,
                expectedPreviousHash);
            if (failure is not null)
            {
                return failure;
            }

            expectedPreviousHash = entryHash;
        }

        return new SecurityAuditIntegrityResult(true, entryCount, null);
    }

    private static SecurityAuditIntegrityResult VerifyLegacyJsonlIntegrity(string path, bool requireKeyedIntegrity)
    {
        try
        {
            string? expectedPreviousHash = null;
            var lineNumber = 0;
            foreach (var entry in ReadLegacyEntries(path))
            {
                lineNumber++;
                var failure = ValidateEntry(
                    lineNumber,
                    requireKeyedIntegrity,
                    entry.TimestampUtc,
                    entry.EventType,
                    entry.Outcome,
                    entry.CanonicalFields,
                    entry.Algorithm,
                    entry.PreviousHash,
                    entry.EntryHash,
                    expectedPreviousHash);
                if (failure is not null)
                {
                    return failure;
                }

                expectedPreviousHash = entry.EntryHash;
            }

            return new SecurityAuditIntegrityResult(true, lineNumber, null);
        }
        catch (Exception ex) when (ex is IOException or JsonException or InvalidOperationException or FormatException)
        {
            return new SecurityAuditIntegrityResult(false, 0, "Audit log integrity verification failed.");
        }
    }

    private static IReadOnlyList<StoredAuditEntry> ReadLegacyEntries(string path)
    {
        var entries = new List<StoredAuditEntry>();
        foreach (var line in File.ReadLines(path))
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            using var document = JsonDocument.Parse(line);
            var root = document.RootElement;
            var integrity = root.GetProperty("integrity");
            entries.Add(new StoredAuditEntry(
                root.GetProperty("timestampUtc").GetDateTimeOffset(),
                root.GetRequiredString("eventType"),
                root.GetRequiredString("outcome"),
                CanonicalizeFields(root.GetProperty("fields")),
                integrity.GetRequiredString("algorithm"),
                integrity.TryGetString("previousHash"),
                integrity.GetRequiredString("entryHash")));
        }

        return entries;
    }

    private static SecurityAuditIntegrityResult? ValidateEntry(
        int entryNumber,
        bool requireKeyedIntegrity,
        DateTimeOffset timestampUtc,
        string eventType,
        string outcome,
        string canonicalFields,
        string algorithm,
        string? previousHash,
        string entryHash,
        string? expectedPreviousHash)
    {
        if (!IsSupportedIntegrityAlgorithm(algorithm))
        {
            return IntegrityFailure(entryNumber, requireKeyedIntegrity, "Audit entry is missing supported integrity metadata.");
        }

        if (requireKeyedIntegrity && !string.Equals(algorithm, HmacSha256Algorithm, StringComparison.Ordinal))
        {
            return IntegrityFailure(entryNumber, requireKeyedIntegrity, "Audit entry is not keyed.");
        }

        if (!string.Equals(previousHash, expectedPreviousHash, StringComparison.Ordinal))
        {
            return IntegrityFailure(entryNumber, requireKeyedIntegrity, "Audit entry previous hash does not match prior entry.");
        }

        var expectedEntryHash = ComputeEntryHash(timestampUtc, eventType, outcome, canonicalFields, previousHash, algorithm);
        return string.Equals(entryHash, expectedEntryHash, StringComparison.Ordinal)
            ? null
            : IntegrityFailure(entryNumber, requireKeyedIntegrity, "Audit entry hash does not match entry content.");
    }

    private static void ConfigureAuditDurability(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            PRAGMA busy_timeout = 30000;
            PRAGMA synchronous = FULL;
            """;
        command.ExecuteNonQuery();
    }

    private static void HardenAuditDatabaseFiles(string databasePath)
    {
        RuntimeFileSecurity.HardenFile(databasePath);
        if (File.Exists(databasePath + "-wal"))
        {
            RuntimeFileSecurity.HardenFile(databasePath + "-wal");
        }

        if (File.Exists(databasePath + "-shm"))
        {
            RuntimeFileSecurity.HardenFile(databasePath + "-shm");
        }
    }

    private static bool CanOpenAsSqliteDatabase(string path)
    {
        try
        {
            using var connection = SqliteConnections.Open(path, SqliteOpenMode.ReadOnly);
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "PRAGMA schema_version;";
            _ = command.ExecuteScalar();
            return true;
        }
        catch (SqliteException)
        {
            return false;
        }
    }

    private static string ResolveIntegrityAlgorithm()
    {
        var hasIntegrityKey = !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(IntegrityKeyEnvironmentVariable));
        if (!hasIntegrityKey && IsProductionEnvironment())
        {
            throw new InvalidOperationException("Security audit integrity validation failed.");
        }

        return hasIntegrityKey ? HmacSha256Algorithm : Sha256Algorithm;
    }

    private static SecurityAuditIntegrityResult IntegrityFailure(int entryNumber, bool requireKeyedIntegrity, string detailedError) =>
        new(false, entryNumber, requireKeyedIntegrity ? "Audit log integrity verification failed." : detailedError);

    private static bool IsProductionEnvironment() =>
        string.Equals(
            Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT") ?? Environment.GetEnvironmentVariable("DOTNET_ENVIRONMENT"),
            "Production",
            StringComparison.OrdinalIgnoreCase);

    private static bool IsSupportedIntegrityAlgorithm(string? algorithm) =>
        string.Equals(algorithm, Sha256Algorithm, StringComparison.Ordinal) ||
        string.Equals(algorithm, HmacSha256Algorithm, StringComparison.Ordinal);

    private static string ComputeEntryHash(
        DateTimeOffset timestampUtc,
        string eventType,
        string outcome,
        string canonicalFields,
        string? previousHash,
        string algorithm)
    {
        var canonicalEntry = string.Join(
            "\n",
            timestampUtc.ToString("O"),
            eventType,
            outcome,
            canonicalFields,
            previousHash ?? string.Empty);
        return string.Equals(algorithm, HmacSha256Algorithm, StringComparison.Ordinal)
            ? ComputeHmacSha256(canonicalEntry)
            : ComputeSha256(canonicalEntry);
    }

    private static string ComputeSha256(string value)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(value));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    private static string ComputeHmacSha256(string value)
    {
        var key = Environment.GetEnvironmentVariable(IntegrityKeyEnvironmentVariable);
        if (string.IsNullOrWhiteSpace(key))
        {
            throw new InvalidOperationException($"Audit log integrity verification requires {IntegrityKeyEnvironmentVariable} for HMACSHA256 entries.");
        }

        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(key));
        var bytes = hmac.ComputeHash(Encoding.UTF8.GetBytes(value));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    private static string CanonicalizeFields(IReadOnlyDictionary<string, object?> fields)
    {
        var ordered = fields
            .OrderBy(field => field.Key, StringComparer.Ordinal)
            .ToDictionary(field => field.Key, field => field.Value, StringComparer.Ordinal);
        return JsonSerializer.Serialize(ordered);
    }

    private static string CanonicalizeFields(JsonElement fields)
    {
        if (fields.ValueKind != JsonValueKind.Object)
        {
            return "{}";
        }

        var parts = fields.EnumerateObject()
            .OrderBy(property => property.Name, StringComparer.Ordinal)
            .Select(property => $"{JsonSerializer.Serialize(property.Name)}:{property.Value.GetRawText()}");
        return "{" + string.Join(",", parts) + "}";
    }

    private sealed record StoredAuditEntry(
        DateTimeOffset TimestampUtc,
        string EventType,
        string Outcome,
        string CanonicalFields,
        string Algorithm,
        string? PreviousHash,
        string EntryHash);
}

public sealed record SecurityAuditIntegrityResult(
    bool IsValid,
    int EntryCount,
    string? Error);

public sealed record SecurityAuditEvent(
    DateTimeOffset TimestampUtc,
    string EventType);
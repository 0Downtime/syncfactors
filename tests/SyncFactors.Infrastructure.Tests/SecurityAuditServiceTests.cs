using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using SyncFactors.Infrastructure;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace SyncFactors.Infrastructure.Tests;

public sealed class SecurityAuditServiceTests : IDisposable
{
    private const string SqlitePasswordEnvironmentVariable = "SYNCFACTORS_SQLITE_PASSWORD";
    private readonly string? _previousAuditPath = Environment.GetEnvironmentVariable("SYNCFACTORS_SECURITY_AUDIT_LOG_PATH");
    private readonly string? _previousIntegrityKey = Environment.GetEnvironmentVariable(SecurityAuditService.IntegrityKeyEnvironmentVariable);
    private readonly string? _previousSqlitePassword = Environment.GetEnvironmentVariable(SqlitePasswordEnvironmentVariable);
    private readonly string _tempRoot = Directory.CreateTempSubdirectory("syncfactors-audit-tests").FullName;

    [Fact]
    public void Write_AppendsTamperEvidentHashChain()
    {
        var auditPath = Path.Combine(_tempRoot, "state", "security-audit.jsonl");
        Environment.SetEnvironmentVariable("SYNCFACTORS_SECURITY_AUDIT_LOG_PATH", auditPath);
        var service = new SecurityAuditService(NullLogger<SecurityAuditService>.Instance);

        service.Write("RunQueued", "Success", ("RequestedBy", "operator"), ("DryRun", true));
        service.Write("RunCancelled", "Success", ("RequestedBy", "operator"));

        var databasePath = SecurityAuditService.ResolveAuditPath();
        var entries = ReadAuditEntries(databasePath);
        var result = SecurityAuditService.VerifyIntegrity(databasePath);

        Assert.Equal(2, entries.Count);
        Assert.True(result.IsValid, result.Error);
        Assert.Equal(2, result.EntryCount);
        Assert.Null(entries[0].PreviousHash);
        Assert.Equal(entries[0].EntryHash, entries[1].PreviousHash);
    }

    [Fact]
    public void Write_UsesTransactionalSqliteChainUnderConcurrentWriters()
    {
        var auditPath = Path.Combine(_tempRoot, "state", "security-audit.db");
        Environment.SetEnvironmentVariable("SYNCFACTORS_SECURITY_AUDIT_LOG_PATH", auditPath);
        var apiAudit = new SecurityAuditService(NullLogger<SecurityAuditService>.Instance);
        var workerAudit = new SecurityAuditService(NullLogger<SecurityAuditService>.Instance);

        Parallel.For(
            0,
            200,
            index => (index % 2 == 0 ? apiAudit : workerAudit).Write(
                "MutationIntent",
                "Pending",
                ("Writer", index % 2 == 0 ? "API" : "Worker"),
                ("Index", index)));

        using var connection = new SqliteConnection($"Data Source={auditPath};Mode=ReadOnly");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM security_audit_entries;";

        Assert.Equal(200L, (long)command.ExecuteScalar()!);
        var integrity = SecurityAuditService.VerifyIntegrity(auditPath);
        Assert.True(integrity.IsValid, integrity.Error);
        Assert.Equal(200, integrity.EntryCount);
    }

    [Fact]
    public void Write_MigratesValidatedLegacyJsonlBeforeAppendingToSqlite()
    {
        var legacyPath = Path.Combine(_tempRoot, "state", "security-audit.jsonl");
        Directory.CreateDirectory(Path.GetDirectoryName(legacyPath)!);
        var timestamp = DateTimeOffset.Parse("2026-07-28T12:00:00.0000000+00:00");
        const string canonicalFields = "{\"RequestedBy\":\"legacy-operator\"}";
        var canonicalEntry = string.Join(
            "\n",
            timestamp.ToString("O"),
            "LegacyRunQueued",
            "Success",
            canonicalFields,
            string.Empty);
        var entryHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonicalEntry))).ToLowerInvariant();
        var legacyEntry = new
        {
            timestampUtc = timestamp,
            eventType = "LegacyRunQueued",
            outcome = "Success",
            fields = new { RequestedBy = "legacy-operator" },
            integrity = new { algorithm = "SHA256", previousHash = (string?)null, entryHash }
        };
        File.WriteAllText(legacyPath, JsonSerializer.Serialize(legacyEntry) + Environment.NewLine);
        Environment.SetEnvironmentVariable("SYNCFACTORS_SECURITY_AUDIT_LOG_PATH", legacyPath);
        Environment.SetEnvironmentVariable(SecurityAuditService.IntegrityKeyEnvironmentVariable, "test-integrity-key");

        var service = new SecurityAuditService(NullLogger<SecurityAuditService>.Instance);
        service.Write("CurrentRunQueued", "Success");

        var databasePath = SecurityAuditService.ResolveAuditPath();
        var rows = ReadAuditEntries(databasePath);
        Assert.Equal(["LegacyAuditMigrationBoundary", "CurrentRunQueued"], rows.Select(row => row.EventType));
        Assert.All(rows, row => Assert.Equal("HMACSHA256", row.Algorithm));
        using var boundaryFields = JsonDocument.Parse(rows[0].FieldsJson);
        Assert.Equal(1, boundaryFields.RootElement.GetProperty("SourceEntryCount").GetInt32());
        Assert.Equal("LegacyJsonlSha256Chain", boundaryFields.RootElement.GetProperty("SourceProvenance").GetString());
        var integrity = SecurityAuditService.VerifyIntegrity(databasePath, requireKeyedIntegrity: true);
        Assert.True(integrity.IsValid, integrity.Error);
        Assert.Equal(2, integrity.EntryCount);
        Assert.True(File.Exists(legacyPath));
    }

    [Fact]
    public void ValidateStartup_UpgradesPlaintextAuditDatabaseWhenSqlCipherIsEnabled()
    {
        var auditPath = Path.Combine(_tempRoot, "state", "plaintext-security-audit.db");
        Environment.SetEnvironmentVariable("SYNCFACTORS_SECURITY_AUDIT_LOG_PATH", auditPath);
        Environment.SetEnvironmentVariable(SecurityAuditService.IntegrityKeyEnvironmentVariable, "test-integrity-key");
        var service = new SecurityAuditService(NullLogger<SecurityAuditService>.Instance);
        service.Write("RunQueued", "Success");
        Assert.True(HasPlaintextSqliteHeader(auditPath));

        Environment.SetEnvironmentVariable(SqlitePasswordEnvironmentVariable, "test-sqlcipher-upgrade-password");
        SecurityAuditService.ValidateStartup(isProduction: true);

        Assert.False(HasPlaintextSqliteHeader(auditPath));
        var integrity = SecurityAuditService.VerifyIntegrity(auditPath, requireKeyedIntegrity: true);
        Assert.True(integrity.IsValid, integrity.Error);
        Assert.Equal(1, integrity.EntryCount);
        Assert.NotEmpty(Directory.EnumerateFiles(Path.GetDirectoryName(auditPath)!, $"{Path.GetFileName(auditPath)}.plaintext-*.bak"));
    }

    [Fact]
    public void ValidateStartup_RecoversInterruptedAuditSqlCipherMigrationBeforeOpeningTheDatabase()
    {
        var auditPath = Path.Combine(_tempRoot, "state", "interrupted-security-audit.db");
        Environment.SetEnvironmentVariable("SYNCFACTORS_SECURITY_AUDIT_LOG_PATH", auditPath);
        Environment.SetEnvironmentVariable(SecurityAuditService.IntegrityKeyEnvironmentVariable, "test-integrity-key");
        var service = new SecurityAuditService(NullLogger<SecurityAuditService>.Instance);
        service.Write("RunQueued", "Success");

        var interruptedBackupPath = $"{auditPath}.plaintext-interrupted.bak";
        File.Move(auditPath, interruptedBackupPath);
        File.WriteAllText($"{auditPath}.encrypted-interrupted.tmp", "incomplete conversion output");
        Environment.SetEnvironmentVariable(SqlitePasswordEnvironmentVariable, "test-sqlcipher-upgrade-password");

        SecurityAuditService.ValidateStartup(isProduction: true);

        Assert.False(HasPlaintextSqliteHeader(auditPath));
        var integrity = SecurityAuditService.VerifyIntegrity(auditPath, requireKeyedIntegrity: true);
        Assert.True(integrity.IsValid, integrity.Error);
        Assert.Equal(1, integrity.EntryCount);
    }

    [Fact]
    public void ValidateStartup_ConcurrentApiAndWorkerStartupSerializeAuditSqlCipherMigration()
    {
        var auditPath = Path.Combine(_tempRoot, "state", "concurrent-security-audit.db");
        Environment.SetEnvironmentVariable("SYNCFACTORS_SECURITY_AUDIT_LOG_PATH", auditPath);
        Environment.SetEnvironmentVariable(SecurityAuditService.IntegrityKeyEnvironmentVariable, "test-integrity-key");
        new SecurityAuditService(NullLogger<SecurityAuditService>.Instance).Write("RunQueued", "Success");
        Environment.SetEnvironmentVariable(SqlitePasswordEnvironmentVariable, "test-sqlcipher-upgrade-password");

        Parallel.For(0, 8, _ => SecurityAuditService.ValidateStartup(isProduction: true));

        Assert.False(HasPlaintextSqliteHeader(auditPath));
        var integrity = SecurityAuditService.VerifyIntegrity(auditPath, requireKeyedIntegrity: true);
        Assert.True(integrity.IsValid, integrity.Error);
        Assert.Equal(1, integrity.EntryCount);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("incorrect-sqlcipher-password")]
    public void ValidateStartup_ProductionFailsClosedWhenEncryptedAuditDatabaseCannotBeOpened(string? configuredPassword)
    {
        var auditPath = Path.Combine(_tempRoot, "state", "encrypted-security-audit.db");
        Environment.SetEnvironmentVariable("SYNCFACTORS_SECURITY_AUDIT_LOG_PATH", auditPath);
        Environment.SetEnvironmentVariable(SecurityAuditService.IntegrityKeyEnvironmentVariable, "test-integrity-key");
        Environment.SetEnvironmentVariable(SqlitePasswordEnvironmentVariable, "correct-sqlcipher-password");
        new SecurityAuditService(NullLogger<SecurityAuditService>.Instance).Write("RunQueued", "Success");
        Environment.SetEnvironmentVariable(SqlitePasswordEnvironmentVariable, configuredPassword);

        var exception = Assert.Throws<InvalidOperationException>(() => SecurityAuditService.ValidateStartup(isProduction: true));

        Assert.Equal("Security audit integrity validation failed.", exception.Message);
    }

    [Fact]
    public void Write_DoesNotReprocessLegacyFileAfterCommittedMigration()
    {
        var legacyPath = Path.Combine(_tempRoot, "state", "completed-migration.jsonl");
        Directory.CreateDirectory(Path.GetDirectoryName(legacyPath)!);
        File.WriteAllText(legacyPath, string.Empty);
        Environment.SetEnvironmentVariable("SYNCFACTORS_SECURITY_AUDIT_LOG_PATH", legacyPath);
        var service = new SecurityAuditService(NullLogger<SecurityAuditService>.Instance);
        service.Write("FirstSqliteEntry", "Success");
        File.WriteAllText(legacyPath, "{ invalid legacy residue }");

        service.Write("SecondSqliteEntry", "Success");

        var rows = ReadAuditEntries(SecurityAuditService.ResolveAuditPath());
        Assert.Equal(["FirstSqliteEntry", "SecondSqliteEntry"], rows.Select(row => row.EventType));
    }

    [Fact]
    public void ValidateStartup_RejectsTamperedLegacyJsonlWithoutPersistingMigrationBoundary()
    {
        var legacyPath = Path.Combine(_tempRoot, "state", "tampered-legacy.jsonl");
        Directory.CreateDirectory(Path.GetDirectoryName(legacyPath)!);
        File.WriteAllText(
            legacyPath,
            """
            {"timestampUtc":"2026-07-28T12:00:00.0000000+00:00","eventType":"LegacyRunQueued","outcome":"Success","fields":{},"integrity":{"algorithm":"SHA256","previousHash":null,"entryHash":"tampered"}}
            """ + Environment.NewLine);
        Environment.SetEnvironmentVariable("SYNCFACTORS_SECURITY_AUDIT_LOG_PATH", legacyPath);
        Environment.SetEnvironmentVariable(SecurityAuditService.IntegrityKeyEnvironmentVariable, "test-integrity-key");

        var exception = Assert.Throws<InvalidOperationException>(() => SecurityAuditService.ValidateStartup(isProduction: true));

        Assert.Equal("Security audit integrity validation failed.", exception.Message);
        Assert.Empty(ReadAuditEntries(SecurityAuditService.ResolveAuditPath()));
        Assert.Equal(0, ReadAuditMetadataCount(SecurityAuditService.ResolveAuditPath()));
        Assert.True(File.Exists(legacyPath));
    }

    [Fact]
    public void VerifyIntegrity_DetectsTamperedAuditContent()
    {
        var auditPath = Path.Combine(_tempRoot, "state", "security-audit.jsonl");
        Environment.SetEnvironmentVariable("SYNCFACTORS_SECURITY_AUDIT_LOG_PATH", auditPath);
        var service = new SecurityAuditService(NullLogger<SecurityAuditService>.Instance);
        service.Write("RunQueued", "Success", ("RequestedBy", "operator"));

        var databasePath = SecurityAuditService.ResolveAuditPath();
        ExecuteAuditSql(databasePath, "UPDATE security_audit_entries SET event_type = 'RunDeleted' WHERE sequence = 1;");

        var result = SecurityAuditService.VerifyIntegrity(databasePath);

        Assert.False(result.IsValid);
        Assert.Equal(1, result.EntryCount);
        Assert.Contains("hash", result.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Write_RejectsTamperedExistingChainBeforeAppending()
    {
        var auditPath = Path.Combine(_tempRoot, "state", "tampered-before-append.db");
        Environment.SetEnvironmentVariable("SYNCFACTORS_SECURITY_AUDIT_LOG_PATH", auditPath);
        var service = new SecurityAuditService(NullLogger<SecurityAuditService>.Instance);
        service.Write("RunQueued", "Success");
        ExecuteAuditSql(auditPath, "UPDATE security_audit_entries SET event_type = 'Tampered' WHERE sequence = 1;");

        var exception = Assert.Throws<InvalidOperationException>(() =>
            service.Write("RunCancelled", "Success"));

        Assert.Equal("Security audit integrity validation failed.", exception.Message);
        Assert.Single(ReadAuditEntries(auditPath));
    }

    [Fact]
    public void Write_UsesHmacIntegrity_WhenIntegrityKeyIsConfigured()
    {
        var auditPath = Path.Combine(_tempRoot, "state", "security-audit.jsonl");
        Environment.SetEnvironmentVariable("SYNCFACTORS_SECURITY_AUDIT_LOG_PATH", auditPath);
        Environment.SetEnvironmentVariable(SecurityAuditService.IntegrityKeyEnvironmentVariable, "test-integrity-key");
        var service = new SecurityAuditService(NullLogger<SecurityAuditService>.Instance);

        service.Write("RunQueued", "Success");

        var databasePath = SecurityAuditService.ResolveAuditPath();
        var entry = Assert.Single(ReadAuditEntries(databasePath));
        var result = SecurityAuditService.VerifyIntegrity(databasePath);

        Assert.True(result.IsValid, result.Error);
        Assert.Equal("HMACSHA256", entry.Algorithm);
    }

    [Fact]
    public void VerifyIntegrity_RequiresKeyedEntries_WhenProductionIntegrityIsRequired()
    {
        var auditPath = Path.Combine(_tempRoot, "state", "security-audit.jsonl");
        Environment.SetEnvironmentVariable("SYNCFACTORS_SECURITY_AUDIT_LOG_PATH", auditPath);
        var service = new SecurityAuditService(NullLogger<SecurityAuditService>.Instance);
        service.Write("RunQueued", "Success");

        var result = SecurityAuditService.VerifyIntegrity(SecurityAuditService.ResolveAuditPath(), requireKeyedIntegrity: true);

        Assert.False(result.IsValid);
        Assert.Equal("Audit log integrity verification failed.", result.Error);
    }

    [Fact]
    public void VerifyIntegrity_OpensEncryptedAuditDatabaseWithConfiguredSqlitePassword()
    {
        var auditPath = Path.Combine(_tempRoot, "state", "encrypted-security-audit.db");
        Environment.SetEnvironmentVariable("SYNCFACTORS_SECURITY_AUDIT_LOG_PATH", auditPath);
        Environment.SetEnvironmentVariable(SqlitePasswordEnvironmentVariable, "test-sqlite-password");
        var service = new SecurityAuditService(NullLogger<SecurityAuditService>.Instance);

        service.Write("RunQueued", "Success");

        var result = SecurityAuditService.VerifyIntegrity(auditPath);

        Assert.True(result.IsValid, result.Error);
        Assert.Equal(1, result.EntryCount);
    }

    [Fact]
    public void ReadEventsSince_ReturnsEventsFromEncryptedSqliteAuditDatabase()
    {
        var auditPath = Path.Combine(_tempRoot, "state", "encrypted-audit-reader.db");
        Environment.SetEnvironmentVariable("SYNCFACTORS_SECURITY_AUDIT_LOG_PATH", auditPath);
        Environment.SetEnvironmentVariable(SqlitePasswordEnvironmentVariable, "test-sqlite-password");
        var service = new SecurityAuditService(NullLogger<SecurityAuditService>.Instance);
        var startedAt = DateTimeOffset.UtcNow.AddSeconds(-1);

        service.Write("RunQueued", "Success");

        var events = SecurityAuditService.ReadEventsSince(auditPath, startedAt);

        var auditEvent = Assert.Single(events);
        Assert.Equal("RunQueued", auditEvent.EventType);
        Assert.True(auditEvent.TimestampUtc >= startedAt);
    }

    [Fact]
    public void ValidateStartup_RejectsMissingKeyInProductionWithoutExposingConfigurationDetails()
    {
        Environment.SetEnvironmentVariable(SecurityAuditService.IntegrityKeyEnvironmentVariable, null);

        var exception = Assert.Throws<InvalidOperationException>(() =>
            SecurityAuditService.ValidateStartup(isProduction: true));

        Assert.Equal("Security audit integrity validation failed.", exception.Message);
    }

    [Fact]
    public void ValidateStartup_RejectsTamperedAuditLogWithoutExposingEntryContent()
    {
        var auditPath = Path.Combine(_tempRoot, "state", "security-audit.jsonl");
        Environment.SetEnvironmentVariable("SYNCFACTORS_SECURITY_AUDIT_LOG_PATH", auditPath);
        Environment.SetEnvironmentVariable(SecurityAuditService.IntegrityKeyEnvironmentVariable, "test-integrity-key");
        var service = new SecurityAuditService(NullLogger<SecurityAuditService>.Instance);
        service.Write("RunQueued", "Success", ("RequestedBy", "sensitive-user@example.com"));
        ExecuteAuditSql(
            SecurityAuditService.ResolveAuditPath(),
            "UPDATE security_audit_entries SET fields_json = '{\"RequestedBy\":\"changed\"}' WHERE sequence = 1;");

        var exception = Assert.Throws<InvalidOperationException>(() =>
            SecurityAuditService.ValidateStartup(isProduction: true));

        Assert.Equal("Security audit integrity validation failed.", exception.Message);
        Assert.DoesNotContain("sensitive-user@example.com", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Write_HardensAuditFilePermissions_WhenUnixFileModesAreAvailable()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var auditPath = Path.Combine(_tempRoot, "state", "security-audit.jsonl");
        Environment.SetEnvironmentVariable("SYNCFACTORS_SECURITY_AUDIT_LOG_PATH", auditPath);
        var service = new SecurityAuditService(NullLogger<SecurityAuditService>.Instance);

        service.Write("RunQueued", "Success");

        Assert.Equal(
            UnixFileMode.UserRead | UnixFileMode.UserWrite,
            File.GetUnixFileMode(SecurityAuditService.ResolveAuditPath()));
        Assert.Equal(
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute,
            File.GetUnixFileMode(Path.GetDirectoryName(SecurityAuditService.ResolveAuditPath())!));
    }

    [Fact]
    public async Task SqliteInitializer_HardensDatabaseFilePermissions_WhenUnixFileModesAreAvailable()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var databasePath = Path.Combine(_tempRoot, "state", "syncfactors.db");
        var initializer = new SqliteDatabaseInitializer(new SqlitePathResolver(databasePath));

        await initializer.InitializeAsync(CancellationToken.None);

        Assert.Equal(
            UnixFileMode.UserRead | UnixFileMode.UserWrite,
            File.GetUnixFileMode(databasePath));
        Assert.Equal(
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute,
            File.GetUnixFileMode(Path.GetDirectoryName(databasePath)!));
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("SYNCFACTORS_SECURITY_AUDIT_LOG_PATH", _previousAuditPath);
        Environment.SetEnvironmentVariable(SecurityAuditService.IntegrityKeyEnvironmentVariable, _previousIntegrityKey);
        Environment.SetEnvironmentVariable(SqlitePasswordEnvironmentVariable, _previousSqlitePassword);
        if (Directory.Exists(_tempRoot))
        {
            Directory.Delete(_tempRoot, recursive: true);
        }
    }

    private static List<AuditEntryRow> ReadAuditEntries(string databasePath)
    {
        using var connection = new SqliteConnection($"Data Source={databasePath};Mode=ReadOnly");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT event_type, algorithm, previous_hash, entry_hash, fields_json FROM security_audit_entries ORDER BY sequence;";
        using var reader = command.ExecuteReader();
        var rows = new List<AuditEntryRow>();
        while (reader.Read())
        {
            rows.Add(new AuditEntryRow(
                reader.GetString(0),
                reader.GetString(1),
                reader.IsDBNull(2) ? null : reader.GetString(2),
                reader.GetString(3),
                reader.GetString(4)));
        }

        return rows;
    }

    private static void ExecuteAuditSql(string databasePath, string sql)
    {
        using var connection = new SqliteConnection($"Data Source={databasePath}");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private static long ReadAuditMetadataCount(string databasePath)
    {
        using var connection = new SqliteConnection($"Data Source={databasePath};Mode=ReadOnly");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM security_audit_metadata;";
        return (long)command.ExecuteScalar()!;
    }

    private static bool HasPlaintextSqliteHeader(string databasePath)
    {
        Span<byte> header = stackalloc byte[16];
        using var file = File.OpenRead(databasePath);
        return file.Read(header) == header.Length && header.SequenceEqual("SQLite format 3\0"u8);
    }

    private sealed record AuditEntryRow(string EventType, string Algorithm, string? PreviousHash, string EntryHash, string FieldsJson);
}

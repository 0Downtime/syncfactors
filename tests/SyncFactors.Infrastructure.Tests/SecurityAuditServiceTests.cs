using Microsoft.Extensions.Logging.Abstractions;
using SyncFactors.Infrastructure;

namespace SyncFactors.Infrastructure.Tests;

public sealed class SecurityAuditServiceTests : IDisposable
{
    private readonly string? _previousAuditPath = Environment.GetEnvironmentVariable("SYNCFACTORS_SECURITY_AUDIT_LOG_PATH");
    private readonly string? _previousIntegrityKey = Environment.GetEnvironmentVariable(SecurityAuditService.IntegrityKeyEnvironmentVariable);
    private readonly string _tempRoot = Directory.CreateTempSubdirectory("syncfactors-audit-tests").FullName;

    [Fact]
    public void Write_AppendsTamperEvidentHashChain()
    {
        var auditPath = Path.Combine(_tempRoot, "state", "security-audit.jsonl");
        Environment.SetEnvironmentVariable("SYNCFACTORS_SECURITY_AUDIT_LOG_PATH", auditPath);
        var service = new SecurityAuditService(NullLogger<SecurityAuditService>.Instance);

        service.Write("RunQueued", "Success", ("RequestedBy", "operator"), ("DryRun", true));
        service.Write("RunCancelled", "Success", ("RequestedBy", "operator"));

        var lines = File.ReadAllLines(auditPath);
        var result = SecurityAuditService.VerifyIntegrity(auditPath);

        Assert.Equal(2, lines.Length);
        Assert.True(result.IsValid, result.Error);
        Assert.Equal(2, result.EntryCount);
        Assert.Contains("\"integrity\"", lines[0], StringComparison.Ordinal);
        Assert.Contains("\"previousHash\":null", lines[0], StringComparison.Ordinal);
    }

    [Fact]
    public void VerifyIntegrity_DetectsTamperedAuditContent()
    {
        var auditPath = Path.Combine(_tempRoot, "state", "security-audit.jsonl");
        Environment.SetEnvironmentVariable("SYNCFACTORS_SECURITY_AUDIT_LOG_PATH", auditPath);
        var service = new SecurityAuditService(NullLogger<SecurityAuditService>.Instance);
        service.Write("RunQueued", "Success", ("RequestedBy", "operator"));

        var tampered = File.ReadAllText(auditPath)
            .Replace("\"RunQueued\"", "\"RunDeleted\"", StringComparison.Ordinal);
        File.WriteAllText(auditPath, tampered);

        var result = SecurityAuditService.VerifyIntegrity(auditPath);

        Assert.False(result.IsValid);
        Assert.Equal(1, result.EntryCount);
        Assert.Contains("hash", result.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Write_UsesHmacIntegrity_WhenIntegrityKeyIsConfigured()
    {
        var auditPath = Path.Combine(_tempRoot, "state", "security-audit.jsonl");
        Environment.SetEnvironmentVariable("SYNCFACTORS_SECURITY_AUDIT_LOG_PATH", auditPath);
        Environment.SetEnvironmentVariable(SecurityAuditService.IntegrityKeyEnvironmentVariable, "test-integrity-key");
        var service = new SecurityAuditService(NullLogger<SecurityAuditService>.Instance);

        service.Write("RunQueued", "Success");

        var line = File.ReadAllText(auditPath);
        var result = SecurityAuditService.VerifyIntegrity(auditPath);

        Assert.True(result.IsValid, result.Error);
        Assert.Contains("\"algorithm\":\"HMACSHA256\"", line, StringComparison.Ordinal);
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
            File.GetUnixFileMode(auditPath));
        Assert.Equal(
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute,
            File.GetUnixFileMode(Path.GetDirectoryName(auditPath)!));
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
        if (Directory.Exists(_tempRoot))
        {
            Directory.Delete(_tempRoot, recursive: true);
        }
    }
}

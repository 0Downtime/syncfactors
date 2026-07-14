using System.Diagnostics;
using SyncFactors.Contracts;
using SyncFactors.Domain;
using SyncFactors.Infrastructure;
using SyncFactors.Worker;

namespace SyncFactors.Api.Tests;

public sealed class WorkerAuditIntegrityTests
{
    [Fact]
    public async Task ProductionWorkerWithMissingIntegrityKey_FailsBeforeDatabaseInitialization()
    {
        await using var fixture = await WorkerStartupFixture.CreateAsync();
        await File.WriteAllTextAsync(fixture.AuditPath, string.Empty);

        var result = await fixture.StartAsync(integrityKey: null);

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("Security audit integrity validation failed.", result.Output, StringComparison.Ordinal);
        Assert.False(File.Exists(fixture.DatabasePath));
    }

    [Fact]
    public async Task ProductionWorkerWithTamperedAuditLog_FailsBeforeDatabaseInitialization()
    {
        await using var fixture = await WorkerStartupFixture.CreateAsync();
        await File.WriteAllTextAsync(fixture.AuditPath, "tampered audit entry");

        var result = await fixture.StartAsync(integrityKey: "test-integrity-key");

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("Security audit integrity validation failed.", result.Output, StringComparison.Ordinal);
        Assert.False(File.Exists(fixture.DatabasePath));
    }

    [Fact]
    public async Task ProductionWorkerWithValidIntegrityKeyAndAudit_AdvancesPastAuditValidationToDatabaseInitialization()
    {
        await using var fixture = await WorkerStartupFixture.CreateAsync();
        await File.WriteAllTextAsync(fixture.AuditPath, string.Empty);

        var result = await fixture.StartAsync(integrityKey: "test-integrity-key");

        Assert.NotEqual(0, result.ExitCode);
        Assert.DoesNotContain("Security audit integrity validation failed.", result.Output, StringComparison.Ordinal);
        Assert.True(File.Exists(fixture.DatabasePath));
    }

    private sealed class WorkerStartupFixture : IAsyncDisposable
    {
        private WorkerStartupFixture(string temporaryDirectory)
        {
            TemporaryDirectory = temporaryDirectory;
            DatabasePath = Path.Combine(temporaryDirectory, "state", "runtime", "syncfactors.db");
            AuditPath = Path.Combine(temporaryDirectory, "state", "runtime", "security-audit.jsonl");
        }

        public string TemporaryDirectory { get; }
        public string DatabasePath { get; }
        public string AuditPath { get; }

        public static Task<WorkerStartupFixture> CreateAsync()
        {
            var temporaryDirectory = Path.Combine(Path.GetTempPath(), "syncfactors-worker-audit-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path.Combine(temporaryDirectory, "state", "runtime"));
            return Task.FromResult(new WorkerStartupFixture(temporaryDirectory));
        }

        public async Task<(int ExitCode, string Output)> StartAsync(string? integrityKey)
        {
            var startInfo = new ProcessStartInfo("dotnet")
            {
                WorkingDirectory = TemporaryDirectory,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            startInfo.ArgumentList.Add("run");
            startInfo.ArgumentList.Add("--project");
            startInfo.ArgumentList.Add(Path.Combine(FindRepositoryRoot(), "src", "SyncFactors.Worker", "SyncFactors.Worker.csproj"));
            startInfo.ArgumentList.Add("--no-build");
            startInfo.Environment["DOTNET_ENVIRONMENT"] = "Production";
            startInfo.Environment["SYNCFACTORS_SECURITY_AUDIT_LOG_PATH"] = AuditPath;
            startInfo.Environment["SyncFactors__SqlitePath"] = DatabasePath;
            startInfo.Environment["SYNCFACTORS_RUN_PROFILE"] = "mock";
            startInfo.Environment[SecurityAuditService.IntegrityKeyEnvironmentVariable] = integrityKey;

            using var process = Process.Start(startInfo);
            Assert.NotNull(process);
            var standardOutput = process!.StandardOutput.ReadToEndAsync();
            var standardError = process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync();
            return (process.ExitCode, await standardOutput + await standardError);
        }

        private static string FindRepositoryRoot()
        {
            for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
            {
                if (File.Exists(Path.Combine(directory.FullName, "SyncFactors.Next.sln")))
                {
                    return directory.FullName;
                }
            }

            throw new DirectoryNotFoundException("Could not locate the SyncFactors repository root.");
        }

        public ValueTask DisposeAsync()
        {
            if (Directory.Exists(TemporaryDirectory))
            {
                Directory.Delete(TemporaryDirectory, recursive: true);
            }

            return ValueTask.CompletedTask;
        }
    }

    [Fact]
    public async Task AuditedDirectoryCommandGateway_RefusesMutationWhenIntentAuditCannotBeWritten()
    {
        var auditService = new ThrowingSecurityAuditService();
        var innerGateway = new CountingDirectoryCommandGateway();
        var auditedGateway = new AuditedDirectoryCommandGateway(innerGateway, auditService);

        await Assert.ThrowsAsync<InvalidOperationException>(() => auditedGateway.ExecuteAsync(CreateCommand(), CancellationToken.None));

        Assert.Equal(0, innerGateway.ExecuteCallCount);
    }

    [Fact]
    public async Task AuditedDirectoryCommandGateway_RecordsFailureOutcomeWhenMutationFails()
    {
        var auditEntries = new List<(string EventType, string Outcome, Dictionary<string, object?> Fields)>();
        var auditService = new CapturingSecurityAuditService(auditEntries);
        var innerGateway = new FailingDirectoryCommandGateway();
        var auditedGateway = new AuditedDirectoryCommandGateway(innerGateway, auditService);

        var command = CreateCommand(action: "Disable");

        var result = await auditedGateway.ExecuteAsync(command, CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Equal(2, auditEntries.Count);
        Assert.Equal("MutationIntent", auditEntries[0].EventType);
        Assert.Equal("Authorized", auditEntries[0].Outcome);
        Assert.Equal("DirectoryMutation", auditEntries[1].EventType);
        Assert.Equal("Failure", auditEntries[1].Outcome);
        Assert.Equal("SyncFactors.Worker", auditEntries[1].Fields["Actor"]);
        Assert.Equal("Disable", auditEntries[1].Fields["Action"]);
        Assert.Equal(auditEntries[0].Fields["CorrelationId"], auditEntries[1].Fields["CorrelationId"]);
    }

    [Fact]
    public async Task AuditedDirectoryCommandGateway_ReportsUnknownOutcomeWhenTerminalAuditWriteFails()
    {
        var auditService = new FailsOnSecondWriteSecurityAuditService();
        var innerGateway = new CountingDirectoryCommandGateway();
        var auditedGateway = new AuditedDirectoryCommandGateway(innerGateway, auditService);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => auditedGateway.ExecuteAsync(CreateCommand(), CancellationToken.None));

        Assert.Equal("Directory mutation outcome is unknown because its audit evidence could not be recorded.", exception.Message);
        Assert.Equal(1, innerGateway.ExecuteCallCount);
        Assert.Single(auditService.Entries);
        Assert.Equal("MutationIntent", auditService.Entries[0].EventType);
    }

    [Fact]
    public async Task AuditedDirectoryCommandGateway_RecordsCorrelatedFailureWhenMutationThrows()
    {
        var auditEntries = new List<(string EventType, string Outcome, Dictionary<string, object?> Fields)>();
        var auditService = new CapturingSecurityAuditService(auditEntries);
        var auditedGateway = new AuditedDirectoryCommandGateway(new ThrowingDirectoryCommandGateway(), auditService);

        await Assert.ThrowsAsync<InvalidOperationException>(() => auditedGateway.ExecuteAsync(CreateCommand(), CancellationToken.None));

        Assert.Equal(2, auditEntries.Count);
        Assert.Equal("MutationIntent", auditEntries[0].EventType);
        Assert.Equal("DirectoryMutation", auditEntries[1].EventType);
        Assert.Equal("Failure", auditEntries[1].Outcome);
        Assert.Equal("SyncFactors.Worker", auditEntries[1].Fields["Actor"]);
        Assert.Equal("jdoe", auditEntries[1].Fields["Target"]);
        Assert.Equal(auditEntries[0].Fields["CorrelationId"], auditEntries[1].Fields["CorrelationId"]);
    }

    private sealed class CapturingSecurityAuditService : ISecurityAuditService
    {
        private readonly List<(string EventType, string Outcome, Dictionary<string, object?> Fields)> _entries;

        public CapturingSecurityAuditService(List<(string EventType, string Outcome, Dictionary<string, object?> Fields)> entries)
        {
            _entries = entries;
        }

        public void Write(string eventType, string outcome, params (string Key, object? Value)[] fields)
        {
            var dict = fields
                .Where(f => f.Value is not null)
                .ToDictionary(f => f.Key, f => f.Value, StringComparer.OrdinalIgnoreCase);
            _entries.Add((eventType, outcome, dict));
        }
    }

    private sealed class ThrowingSecurityAuditService : ISecurityAuditService
    {
        public void Write(string eventType, string outcome, params (string Key, object? Value)[] fields)
        {
            _ = eventType;
            _ = outcome;
            _ = fields;
            throw new InvalidOperationException("Audit storage is unavailable.");
        }
    }

    private sealed class FailsOnSecondWriteSecurityAuditService : ISecurityAuditService
    {
        public List<(string EventType, string Outcome, Dictionary<string, object?> Fields)> Entries { get; } = [];

        public void Write(string eventType, string outcome, params (string Key, object? Value)[] fields)
        {
            if (Entries.Count == 1)
            {
                throw new InvalidOperationException("Audit storage is unavailable.");
            }

            Entries.Add((
                eventType,
                outcome,
                fields.Where(field => field.Value is not null)
                    .ToDictionary(field => field.Key, field => field.Value, StringComparer.OrdinalIgnoreCase)));
        }
    }

    private sealed class CountingDirectoryCommandGateway : IDirectoryCommandGateway
    {
        public int ExecuteCallCount { get; private set; }

        public Task<DirectoryCommandResult> ExecuteAsync(DirectoryMutationCommand command, CancellationToken cancellationToken)
        {
            _ = cancellationToken;
            ExecuteCallCount++;
            return Task.FromResult(new DirectoryCommandResult(
                Succeeded: true,
                Action: command.Action,
                SamAccountName: command.SamAccountName,
                DistinguishedName: null,
                Message: "OK",
                RunId: null));
        }
    }

    private static DirectoryMutationCommand CreateCommand(string action = "Create") => new(
        Action: action,
        WorkerId: "10001",
        ManagerId: null,
        ManagerDistinguishedName: null,
        SamAccountName: "jdoe",
        CommonName: "John Doe",
        UserPrincipalName: "jdoe@example.com",
        Mail: "jdoe@example.com",
        TargetOu: "OU=Users,DC=example,DC=com",
        DisplayName: "John Doe",
        CurrentDistinguishedName: null,
        EnableAccount: true,
        Operations: [],
        Attributes: new Dictionary<string, string?>());

    private sealed class StubDirectoryCommandGateway : IDirectoryCommandGateway
    {
        public Task<DirectoryCommandResult> ExecuteAsync(DirectoryMutationCommand command, CancellationToken cancellationToken)
        {
            _ = cancellationToken;
            return Task.FromResult(new DirectoryCommandResult(
                Succeeded: true,
                Action: command.Action,
                SamAccountName: command.SamAccountName,
                DistinguishedName: "CN=" + command.CommonName + "," + command.TargetOu,
                Message: "OK",
                RunId: null));
        }
    }

    private sealed class FailingDirectoryCommandGateway : IDirectoryCommandGateway
    {
        public Task<DirectoryCommandResult> ExecuteAsync(DirectoryMutationCommand command, CancellationToken cancellationToken)
        {
            _ = cancellationToken;
            return Task.FromResult(new DirectoryCommandResult(
                Succeeded: false,
                Action: command.Action,
                SamAccountName: command.SamAccountName,
                DistinguishedName: null,
                Message: "LDAP connection failed",
                RunId: null));
        }
    }

    private sealed class ThrowingDirectoryCommandGateway : IDirectoryCommandGateway
    {
        public Task<DirectoryCommandResult> ExecuteAsync(DirectoryMutationCommand command, CancellationToken cancellationToken)
        {
            _ = command;
            _ = cancellationToken;
            throw new InvalidOperationException("Directory command failed.");
        }
    }
}

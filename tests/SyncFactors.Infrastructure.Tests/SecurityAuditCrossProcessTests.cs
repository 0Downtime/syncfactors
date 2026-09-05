using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using SyncFactors.Infrastructure;
using System.Diagnostics;

namespace SyncFactors.Infrastructure.Tests;

public sealed class SecurityAuditCrossProcessTests
{
    private const string ProbeEnvironmentVariable = "SYNCFACTORS_AUDIT_WRITER_PROBE";
    private const string ProbeWriterEnvironmentVariable = "SYNCFACTORS_AUDIT_WRITER_NAME";
    private const string ProbeCountEnvironmentVariable = "SYNCFACTORS_AUDIT_WRITER_COUNT";

    [Fact]
    public async Task ApiAndWorkerProcesses_AppendOneValidChainUnderContention()
    {
        var tempRoot = Directory.CreateTempSubdirectory("syncfactors-audit-process-tests").FullName;
        var auditPath = Path.Combine(tempRoot, "security-audit.db");
        try
        {
            using var api = StartWriterProcess("API", auditPath, entryCount: 50);
            using var worker = StartWriterProcess("Worker", auditPath, entryCount: 50);

            var apiOutput = await api.StandardOutput.ReadToEndAsync();
            var apiError = await api.StandardError.ReadToEndAsync();
            var workerOutput = await worker.StandardOutput.ReadToEndAsync();
            var workerError = await worker.StandardError.ReadToEndAsync();
            await Task.WhenAll(api.WaitForExitAsync(), worker.WaitForExitAsync());

            Assert.True(api.ExitCode == 0, $"API writer process failed. stdout={apiOutput} stderr={apiError}");
            Assert.True(worker.ExitCode == 0, $"Worker writer process failed. stdout={workerOutput} stderr={workerError}");

            var integrity = SecurityAuditService.VerifyIntegrity(auditPath);
            Assert.True(integrity.IsValid, integrity.Error);
            Assert.Equal(100, integrity.EntryCount);

            using var connection = new SqliteConnection($"Data Source={auditPath};Mode=ReadOnly");
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText =
                """
                SELECT json_extract(fields_json, '$.Writer'), COUNT(*)
                FROM security_audit_entries
                GROUP BY json_extract(fields_json, '$.Writer')
                ORDER BY 1;
                """;
            await using var reader = await command.ExecuteReaderAsync();
            var writerCounts = new Dictionary<string, long>(StringComparer.Ordinal);
            while (await reader.ReadAsync())
            {
                writerCounts[reader.GetString(0)] = reader.GetInt64(1);
            }

            Assert.Equal(50, writerCounts["API"]);
            Assert.Equal(50, writerCounts["Worker"]);
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public void WriterProbe()
    {
        if (!string.Equals(Environment.GetEnvironmentVariable(ProbeEnvironmentVariable), "1", StringComparison.Ordinal))
        {
            return;
        }

        var writer = Environment.GetEnvironmentVariable(ProbeWriterEnvironmentVariable)
            ?? throw new InvalidOperationException("Writer probe name is missing.");
        var count = int.Parse(Environment.GetEnvironmentVariable(ProbeCountEnvironmentVariable) ?? "0");
        var audit = new SecurityAuditService(NullLogger<SecurityAuditService>.Instance);
        for (var index = 0; index < count; index++)
        {
            audit.Write(
                "MutationIntent",
                "Pending",
                ("Writer", writer),
                ("CorrelationId", $"{writer}-{index}"));
        }
    }

    private static Process StartWriterProcess(string writer, string auditPath, int entryCount)
    {
        var repositoryRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
        var projectPath = Path.Combine(repositoryRoot, "tests", "SyncFactors.Infrastructure.Tests", "SyncFactors.Infrastructure.Tests.csproj");
        var startInfo = new ProcessStartInfo
        {
            FileName = "dotnet",
            WorkingDirectory = repositoryRoot,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        startInfo.ArgumentList.Add("test");
        startInfo.ArgumentList.Add(projectPath);
        startInfo.ArgumentList.Add("-c");
        startInfo.ArgumentList.Add("Release");
        startInfo.ArgumentList.Add("--no-build");
        startInfo.ArgumentList.Add("--filter");
        startInfo.ArgumentList.Add("FullyQualifiedName~SecurityAuditCrossProcessTests.WriterProbe");
        startInfo.Environment[ProbeEnvironmentVariable] = "1";
        startInfo.Environment[ProbeWriterEnvironmentVariable] = writer;
        startInfo.Environment[ProbeCountEnvironmentVariable] = entryCount.ToString(System.Globalization.CultureInfo.InvariantCulture);
        startInfo.Environment["SYNCFACTORS_SECURITY_AUDIT_LOG_PATH"] = auditPath;
        startInfo.Environment["ASPNETCORE_ENVIRONMENT"] = "Development";
        startInfo.Environment.Remove(SecurityAuditService.IntegrityKeyEnvironmentVariable);

        return Process.Start(startInfo)
            ?? throw new InvalidOperationException($"Could not start the {writer} audit writer process.");
    }
}

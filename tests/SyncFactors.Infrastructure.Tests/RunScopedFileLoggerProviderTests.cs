using Microsoft.Extensions.Logging;

namespace SyncFactors.Infrastructure.Tests;

public sealed class RunScopedFileLoggerProviderTests
{
    [Fact]
    public void Log_WithRunScope_WritesPerRunLogFile()
    {
        var logRoot = Path.Combine(Path.GetTempPath(), "syncfactors-run-logs", Guid.NewGuid().ToString("N"));
        using var provider = new RunScopedFileLoggerProvider(logRoot);
        provider.SetScopeProvider(new LoggerExternalScopeProvider());
        var logger = provider.CreateLogger("Tests.RunScoped");

        using (logger.BeginScope(new Dictionary<string, object?> { ["RunId"] = "bulk-20260421010101000" }))
        {
            logger.LogInformation("Per-run logging is enabled.");
        }

        var logPath = LocalFileLogging.ResolveRunLogPath("bulk-20260421010101000", logRoot);
        Assert.True(File.Exists(logPath));
        var contents = File.ReadAllText(logPath);
        Assert.Contains("Per-run logging is enabled.", contents, StringComparison.Ordinal);
        Assert.Contains("Tests.RunScoped", contents, StringComparison.Ordinal);
    }

    [Fact]
    public void Log_WithRunScope_RedactsPii()
    {
        var logRoot = Path.Combine(Path.GetTempPath(), "syncfactors-run-logs", Guid.NewGuid().ToString("N"));
        using var provider = new RunScopedFileLoggerProvider(logRoot);
        provider.SetScopeProvider(new LoggerExternalScopeProvider());
        var logger = provider.CreateLogger("Tests.RunScoped");

        using (logger.BeginScope(new Dictionary<string, object?> { ["RunId"] = "bulk-20260421010101000" }))
        {
            logger.LogInformation(
                "Prepared AD user. WorkerId={WorkerId} SamAccountName={SamAccountName} UserPrincipalName={UserPrincipalName}",
                "10001",
                "jdoe",
                "jane.doe@example.local");
        }

        var logPath = LocalFileLogging.ResolveRunLogPath("bulk-20260421010101000", logRoot);
        var contents = File.ReadAllText(logPath);
        Assert.Contains("WorkerId=[REDACTED:WorkerId]", contents, StringComparison.Ordinal);
        Assert.Contains("SamAccountName=[REDACTED:SamAccountName]", contents, StringComparison.Ordinal);
        Assert.Contains("UserPrincipalName=[REDACTED:UserPrincipalName]", contents, StringComparison.Ordinal);
        Assert.DoesNotContain("10001", contents, StringComparison.Ordinal);
        Assert.DoesNotContain("jdoe", contents, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("jane.doe@example.local", contents, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Log_WithoutRunScope_DoesNotWritePerRunLogFile()
    {
        var logRoot = Path.Combine(Path.GetTempPath(), "syncfactors-run-logs", Guid.NewGuid().ToString("N"));
        using var provider = new RunScopedFileLoggerProvider(logRoot);
        provider.SetScopeProvider(new LoggerExternalScopeProvider());
        var logger = provider.CreateLogger("Tests.RunScoped");

        logger.LogInformation("This should stay out of per-run logs.");

        var runDirectory = LocalFileLogging.ResolveRunLogDirectory(logRoot);
        Assert.False(Directory.Exists(runDirectory));
    }

    [Fact]
    public void LocalFileLogger_RedactsPii()
    {
        var logRoot = Path.Combine(Path.GetTempPath(), "syncfactors-local-logs", Guid.NewGuid().ToString("N"));
        using var provider = new LocalFileLoggerProvider("api", logRoot);
        provider.SetScopeProvider(new LoggerExternalScopeProvider());
        var logger = provider.CreateLogger("Tests.Local");

        logger.LogWarning(
            "Lookup failed. WorkerId={WorkerId} DistinguishedName={DistinguishedName} Mail={Mail}",
            "10001",
            "CN=Jane Doe,OU=Users,DC=example,DC=local",
            "jane.doe@example.local");

        var logPath = LocalFileLogging.ResolveDatedFilePath("api", logRoot, DateTimeOffset.Now);
        var contents = File.ReadAllText(logPath);
        Assert.Contains("WorkerId=[REDACTED:WorkerId]", contents, StringComparison.Ordinal);
        Assert.Contains("DistinguishedName=[REDACTED:DistinguishedName]", contents, StringComparison.Ordinal);
        Assert.Contains("Mail=[REDACTED:Mail]", contents, StringComparison.Ordinal);
        Assert.DoesNotContain("10001", contents, StringComparison.Ordinal);
        Assert.DoesNotContain("Jane Doe", contents, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("jane.doe@example.local", contents, StringComparison.OrdinalIgnoreCase);
    }
}

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
}

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

    [Fact]
    public void LocalFileLogger_RedactsExceptionPii()
    {
        var logRoot = Path.Combine(Path.GetTempPath(), "syncfactors-local-logs", Guid.NewGuid().ToString("N"));
        using var provider = new LocalFileLoggerProvider("api", logRoot);
        provider.SetScopeProvider(new LoggerExternalScopeProvider());
        var logger = provider.CreateLogger("Tests.Local");
        var exception = new InvalidOperationException("Failed for UserPrincipalName=jane.doe@example.local");

        logger.LogError(exception, "Lookup failed for WorkerId={WorkerId}", "10001");

        var logPath = LocalFileLogging.ResolveDatedFilePath("api", logRoot, DateTimeOffset.Now);
        var contents = File.ReadAllText(logPath);
        Assert.Contains("WorkerId=[REDACTED:WorkerId]", contents, StringComparison.Ordinal);
        Assert.Contains("UserPrincipalName=[REDACTED:UserPrincipalName]", contents, StringComparison.Ordinal);
        Assert.DoesNotContain("jane.doe@example.local", contents, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void PruneDatedFiles_RetainsNewestFiles()
    {
        var logRoot = Path.Combine(Path.GetTempPath(), "syncfactors-local-logs", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(logRoot);
        var oldPath = LocalFileLogging.ResolveDatedFilePath("api", logRoot, DateTimeOffset.UtcNow.AddDays(-2));
        var middlePath = LocalFileLogging.ResolveDatedFilePath("api", logRoot, DateTimeOffset.UtcNow.AddDays(-1));
        var newestPath = LocalFileLogging.ResolveDatedFilePath("api", logRoot, DateTimeOffset.UtcNow);
        File.WriteAllText(oldPath, "old");
        File.WriteAllText(middlePath, "middle");
        File.WriteAllText(newestPath, "newest");
        File.SetLastWriteTimeUtc(oldPath, DateTime.UtcNow.AddDays(-2));
        File.SetLastWriteTimeUtc(middlePath, DateTime.UtcNow.AddDays(-1));
        File.SetLastWriteTimeUtc(newestPath, DateTime.UtcNow);

        LocalFileLogging.PruneDatedFiles("api", logRoot, retainedFileCountLimit: 2);

        Assert.False(File.Exists(oldPath));
        Assert.True(File.Exists(middlePath));
        Assert.True(File.Exists(newestPath));
    }

    [Fact]
    public void PruneDatedFiles_DoesNotTraverseLinkedLogDirectory()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var linkPath = Path.Combine(Path.GetTempPath(), "syncfactors-linked-log-root", Guid.NewGuid().ToString("N"));
        var outsideDirectory = Path.Combine(Path.GetTempPath(), "syncfactors-outside-logs", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.GetDirectoryName(linkPath)!);
        Directory.CreateDirectory(outsideDirectory);
        var oldPath = LocalFileLogging.ResolveDatedFilePath("worker", outsideDirectory, DateTimeOffset.UtcNow.AddDays(-2));
        var newPath = LocalFileLogging.ResolveDatedFilePath("worker", outsideDirectory, DateTimeOffset.UtcNow);
        File.WriteAllText(oldPath, "old");
        File.WriteAllText(newPath, "new");
        File.SetLastWriteTimeUtc(oldPath, DateTime.UtcNow.AddDays(-2));
        File.SetLastWriteTimeUtc(newPath, DateTime.UtcNow);
        Directory.CreateSymbolicLink(linkPath, outsideDirectory);

        LocalFileLogging.PruneDatedFiles("worker", linkPath, retainedFileCountLimit: 1);

        Assert.True(File.Exists(oldPath));
        Assert.True(File.Exists(newPath));
    }

    [Fact]
    public void PruneRunLogFiles_RetainsNewestFiles()
    {
        var logRoot = Path.Combine(Path.GetTempPath(), "syncfactors-run-logs", Guid.NewGuid().ToString("N"));
        var runDirectory = LocalFileLogging.ResolveRunLogDirectory(logRoot);
        Directory.CreateDirectory(runDirectory);
        var oldPath = LocalFileLogging.ResolveRunLogPath("old-run", logRoot);
        var middlePath = LocalFileLogging.ResolveRunLogPath("middle-run", logRoot);
        var newestPath = LocalFileLogging.ResolveRunLogPath("newest-run", logRoot);
        File.WriteAllText(oldPath, "old");
        File.WriteAllText(middlePath, "middle");
        File.WriteAllText(newestPath, "newest");
        File.SetLastWriteTimeUtc(oldPath, DateTime.UtcNow.AddDays(-2));
        File.SetLastWriteTimeUtc(middlePath, DateTime.UtcNow.AddDays(-1));
        File.SetLastWriteTimeUtc(newestPath, DateTime.UtcNow);

        LocalFileLogging.PruneRunLogFiles(logRoot, retainedFileCountLimit: 2);

        Assert.False(File.Exists(oldPath));
        Assert.True(File.Exists(middlePath));
        Assert.True(File.Exists(newestPath));
    }

    [Fact]
    public void PruneRunLogFiles_DoesNotTraverseLinkedRunDirectory()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var logRoot = Path.Combine(Path.GetTempPath(), "syncfactors-linked-logs", Guid.NewGuid().ToString("N"));
        var outsideDirectory = Path.Combine(Path.GetTempPath(), "syncfactors-outside-runs", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(logRoot);
        Directory.CreateDirectory(outsideDirectory);
        var oldPath = Path.Combine(outsideDirectory, "old.log");
        var newPath = Path.Combine(outsideDirectory, "new.log");
        File.WriteAllText(oldPath, "old");
        File.WriteAllText(newPath, "new");
        File.SetLastWriteTimeUtc(oldPath, DateTime.UtcNow.AddDays(-2));
        File.SetLastWriteTimeUtc(newPath, DateTime.UtcNow);
        Directory.CreateSymbolicLink(LocalFileLogging.ResolveRunLogDirectory(logRoot), outsideDirectory);

        LocalFileLogging.PruneRunLogFiles(logRoot, retainedFileCountLimit: 1);

        Assert.True(File.Exists(oldPath));
        Assert.True(File.Exists(newPath));
    }

    [Fact]
    public void PruneExpiredLogFiles_RemovesProcessRunAndPreviewLogsOlderThanRetentionWindow()
    {
        var logRoot = Path.Combine(Path.GetTempPath(), "syncfactors-expiring-logs", Guid.NewGuid().ToString("N"));
        var runDirectory = LocalFileLogging.ResolveRunLogDirectory(logRoot);
        var previewDirectory = LocalFileLogging.ResolvePreviewLogDirectory(logRoot);
        Directory.CreateDirectory(runDirectory);
        Directory.CreateDirectory(previewDirectory);
        var now = new DateTimeOffset(2026, 7, 14, 12, 0, 0, TimeSpan.Zero);
        var expiredProcessPath = LocalFileLogging.ResolveDatedFilePath("worker", logRoot, now.AddDays(-8));
        var currentProcessPath = LocalFileLogging.ResolveDatedFilePath("worker", logRoot, now.AddDays(-6));
        var expiredRunPath = LocalFileLogging.ResolveRunLogPath("expired-run", logRoot);
        var currentRunPath = LocalFileLogging.ResolveRunLogPath("current-run", logRoot);
        var expiredPreviewPath = Path.Combine(previewDirectory, "expired.jsonl");
        var currentPreviewPath = Path.Combine(previewDirectory, "current.jsonl");

        foreach (var path in new[]
                 {
                     expiredProcessPath,
                     currentProcessPath,
                     expiredRunPath,
                     currentRunPath,
                     expiredPreviewPath,
                     currentPreviewPath
                 })
        {
            File.WriteAllText(path, "log");
        }

        foreach (var path in new[] { expiredProcessPath, expiredRunPath, expiredPreviewPath })
        {
            File.SetLastWriteTimeUtc(path, now.AddDays(-8).UtcDateTime);
        }

        foreach (var path in new[] { currentProcessPath, currentRunPath, currentPreviewPath })
        {
            File.SetLastWriteTimeUtc(path, now.AddDays(-6).UtcDateTime);
        }

        LocalFileLogging.PruneExpiredLogFiles(logRoot, retentionDays: 7, now);

        Assert.False(File.Exists(expiredProcessPath));
        Assert.False(File.Exists(expiredRunPath));
        Assert.False(File.Exists(expiredPreviewPath));
        Assert.True(File.Exists(currentProcessPath));
        Assert.True(File.Exists(currentRunPath));
        Assert.True(File.Exists(currentPreviewPath));
    }

    [Fact]
    public void PruneExpiredLogFiles_DoesNotTraverseLinkedLogDirectories()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var logRoot = Path.Combine(Path.GetTempPath(), "syncfactors-linked-logs", Guid.NewGuid().ToString("N"));
        var outsideDirectory = Path.Combine(Path.GetTempPath(), "syncfactors-outside-logs", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(logRoot);
        Directory.CreateDirectory(outsideDirectory);
        var victimPath = Path.Combine(outsideDirectory, "victim.log");
        File.WriteAllText(victimPath, "must remain");
        File.SetLastWriteTimeUtc(victimPath, DateTime.UtcNow.AddDays(-8));
        Directory.CreateSymbolicLink(LocalFileLogging.ResolveRunLogDirectory(logRoot), outsideDirectory);

        LocalFileLogging.PruneExpiredLogFiles(logRoot, retentionDays: 7, DateTimeOffset.UtcNow);

        Assert.True(File.Exists(victimPath));
    }

    [Fact]
    public void PruneExpiredLogFiles_IgnoresUnsupportedRetentionDays()
    {
        var logRoot = Path.Combine(Path.GetTempPath(), "syncfactors-expiring-logs", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(logRoot);
        var oldPath = LocalFileLogging.ResolveDatedFilePath("worker", logRoot, DateTimeOffset.UtcNow.AddDays(-8));
        File.WriteAllText(oldPath, "old");

        LocalFileLogging.PruneExpiredLogFiles(logRoot, int.MaxValue, DateTimeOffset.UtcNow);

        Assert.True(File.Exists(oldPath));
    }

    [Fact]
    public void RunScopedFileLogger_PrunesRunLogsAfterWriting()
    {
        var logRoot = Path.Combine(Path.GetTempPath(), "syncfactors-run-logs", Guid.NewGuid().ToString("N"));
        var runDirectory = LocalFileLogging.ResolveRunLogDirectory(logRoot);
        Directory.CreateDirectory(runDirectory);
        var oldPath = LocalFileLogging.ResolveRunLogPath("old-run", logRoot);
        File.WriteAllText(oldPath, "old");
        File.SetLastWriteTimeUtc(oldPath, DateTime.UtcNow.AddDays(-1));

        using var provider = new RunScopedFileLoggerProvider(logRoot, retainedFileCountLimit: 1);
        provider.SetScopeProvider(new LoggerExternalScopeProvider());
        var logger = provider.CreateLogger("Tests.RunScoped");

        using (logger.BeginScope(new Dictionary<string, object?> { ["RunId"] = "new-run" }))
        {
            logger.LogInformation("New run log.");
        }

        Assert.False(File.Exists(oldPath));
        Assert.True(File.Exists(LocalFileLogging.ResolveRunLogPath("new-run", logRoot)));
    }

    [Fact]
    public void LocalFileLogger_PrunesExpiredLogsWhenDailyProcessLogStarts()
    {
        var logRoot = Path.Combine(Path.GetTempPath(), "syncfactors-expiring-logs", Guid.NewGuid().ToString("N"));
        var runDirectory = LocalFileLogging.ResolveRunLogDirectory(logRoot);
        Directory.CreateDirectory(runDirectory);
        var expiredRunPath = LocalFileLogging.ResolveRunLogPath("expired-run", logRoot);
        File.WriteAllText(expiredRunPath, "old");
        File.SetLastWriteTimeUtc(expiredRunPath, DateTime.UtcNow.AddDays(-3));

        using var provider = new LocalFileLoggerProvider(
            "worker",
            logRoot,
            retainedFileCountLimit: 7,
            retentionDays: 2);
        provider.SetScopeProvider(new LoggerExternalScopeProvider());
        var logger = provider.CreateLogger("Tests.Local");

        logger.LogInformation("Start today's worker log.");

        Assert.False(File.Exists(expiredRunPath));
    }

    [Theory]
    [InlineData(null, 5, 5)]
    [InlineData("", 5, 5)]
    [InlineData("3", 5, 3)]
    [InlineData("0", 5, 5)]
    [InlineData("-2", 5, 5)]
    [InlineData("invalid", 5, 5)]
    public void ResolveRetainedFileCountLimit_UsesPositiveConfiguredValueOnly(
        string? configuredValue,
        int defaultValue,
        int expected)
    {
        Assert.Equal(expected, LocalFileLogging.ResolveRetainedFileCountLimit(configuredValue, defaultValue));
    }

    [Theory]
    [InlineData(null, 7)]
    [InlineData("30", 30)]
    [InlineData("0", 7)]
    [InlineData("-1", 7)]
    [InlineData("36501", 7)]
    [InlineData("2147483647", 7)]
    [InlineData("invalid", 7)]
    public void ResolveLogRetentionDays_UsesOnlySupportedValues(string? configuredValue, int expected)
    {
        Assert.Equal(expected, LocalFileLogging.ResolveLogRetentionDays(configuredValue));
    }

    [Fact]
    public void Configure_WhenRunLoggingDisabled_WritesOnlyProcessLog()
    {
        var logRoot = Path.Combine(Path.GetTempPath(), "syncfactors-local-logs", Guid.NewGuid().ToString("N"));
        using var loggerFactory = LoggerFactory.Create(logging => LocalFileLogging.Configure(
            logging,
            processName: "api",
            enabledValue: "true",
            directoryValue: logRoot,
            retainedFileCountLimitValue: "2",
            runLoggingEnabledValue: "false",
            runRetainedFileCountLimitValue: "1"));
        var logger = loggerFactory.CreateLogger("Tests.Local");

        using (logger.BeginScope(new Dictionary<string, object?> { ["RunId"] = "run-with-disabled-run-log" }))
        {
            logger.LogInformation("Process log only.");
        }

        var processLogPath = LocalFileLogging.ResolveDatedFilePath("api", logRoot, DateTimeOffset.Now);
        Assert.True(File.Exists(processLogPath));
        Assert.False(Directory.Exists(LocalFileLogging.ResolveRunLogDirectory(logRoot)));
    }

    [Fact]
    public void Configure_PrunesLogsOlderThanSevenDaysByDefault()
    {
        var logRoot = Path.Combine(Path.GetTempPath(), "syncfactors-expiring-logs", Guid.NewGuid().ToString("N"));
        var runDirectory = LocalFileLogging.ResolveRunLogDirectory(logRoot);
        var previewDirectory = LocalFileLogging.ResolvePreviewLogDirectory(logRoot);
        Directory.CreateDirectory(runDirectory);
        Directory.CreateDirectory(previewDirectory);
        var expiredRunPath = LocalFileLogging.ResolveRunLogPath("expired-run", logRoot);
        var expiredPreviewPath = Path.Combine(previewDirectory, "expired.jsonl");
        File.WriteAllText(expiredRunPath, "old");
        File.WriteAllText(expiredPreviewPath, "old");
        File.SetLastWriteTimeUtc(expiredRunPath, DateTime.UtcNow.AddDays(-8));
        File.SetLastWriteTimeUtc(expiredPreviewPath, DateTime.UtcNow.AddDays(-8));

        using var loggerFactory = LoggerFactory.Create(logging => LocalFileLogging.Configure(
            logging,
            processName: "worker",
            enabledValue: "true",
            directoryValue: logRoot,
            retainedFileCountLimitValue: null,
            runLoggingEnabledValue: "false",
            runRetainedFileCountLimitValue: null));

        Assert.False(File.Exists(expiredRunPath));
        Assert.False(File.Exists(expiredPreviewPath));
    }

    [Fact]
    public void Configure_UsesConfiguredLogRetentionDays()
    {
        var logRoot = Path.Combine(Path.GetTempPath(), "syncfactors-expiring-logs", Guid.NewGuid().ToString("N"));
        var runDirectory = LocalFileLogging.ResolveRunLogDirectory(logRoot);
        Directory.CreateDirectory(runDirectory);
        var expiredRunPath = LocalFileLogging.ResolveRunLogPath("three-day-old-run", logRoot);
        File.WriteAllText(expiredRunPath, "old");
        File.SetLastWriteTimeUtc(expiredRunPath, DateTime.UtcNow.AddDays(-3));

        using var loggerFactory = LoggerFactory.Create(logging => LocalFileLogging.Configure(
            logging,
            processName: "worker",
            enabledValue: "true",
            directoryValue: logRoot,
            retainedFileCountLimitValue: null,
            runLoggingEnabledValue: "false",
            runRetainedFileCountLimitValue: null,
            retentionDaysValue: "2"));

        Assert.False(File.Exists(expiredRunPath));
    }

    [Fact]
    public void Configure_WhenRunLoggingEnabled_WritesProcessAndRunLogs()
    {
        var logRoot = Path.Combine(Path.GetTempPath(), "syncfactors-local-logs", Guid.NewGuid().ToString("N"));
        using var loggerFactory = LoggerFactory.Create(logging => LocalFileLogging.Configure(
            logging,
            processName: "worker",
            enabledValue: "true",
            directoryValue: logRoot,
            retainedFileCountLimitValue: "2",
            runLoggingEnabledValue: "true",
            runRetainedFileCountLimitValue: "1"));
        var logger = loggerFactory.CreateLogger("Tests.Local");

        using (logger.BeginScope(new Dictionary<string, object?> { ["RunId"] = "run-with-enabled-run-log" }))
        {
            logger.LogInformation("Process and run logs.");
        }

        Assert.True(File.Exists(LocalFileLogging.ResolveDatedFilePath("worker", logRoot, DateTimeOffset.Now)));
        Assert.True(File.Exists(LocalFileLogging.ResolveRunLogPath("run-with-enabled-run-log", logRoot)));
    }
}

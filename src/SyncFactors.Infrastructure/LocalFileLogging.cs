namespace SyncFactors.Infrastructure;

using Microsoft.Extensions.Logging;
using System.Security;

public static class LocalFileLogging
{
    public const string EnabledEnvironmentVariable = "SYNCFACTORS_LOCAL_FILE_LOGGING_ENABLED";
    public const string DirectoryEnvironmentVariable = "SYNCFACTORS_LOCAL_LOG_DIRECTORY";
    public const string RetainedFileCountLimitEnvironmentVariable = "SYNCFACTORS_LOCAL_LOG_RETAINED_FILE_COUNT";
    public const string RetentionDaysEnvironmentVariable = "SYNCFACTORS_LOCAL_LOG_RETENTION_DAYS";
    public const string RunFileLoggingEnabledEnvironmentVariable = "SYNCFACTORS_RUN_FILE_LOGGING_ENABLED";
    public const string RunRetainedFileCountLimitEnvironmentVariable = "SYNCFACTORS_RUN_LOG_RETAINED_FILE_COUNT";
    public const int RetainedFileCountLimit = 7;
    public const int RunRetainedFileCountLimit = 200;
    public const int LogRetentionDays = 7;
    public const int MaximumLogRetentionDays = 36_500;
    private const string RepositoryRootEnvironmentVariable = "REPO_ROOT";

    public static bool IsEnabled(string? configuredValue, bool defaultValue = true)
    {
        if (string.IsNullOrWhiteSpace(configuredValue))
        {
            return defaultValue;
        }

        return configuredValue.Trim().ToLowerInvariant() switch
        {
            "0" => false,
            "off" => false,
            "false" => false,
            "no" => false,
            "1" => true,
            "on" => true,
            "true" => true,
            "yes" => true,
            _ => false
        };
    }

    public static int ResolveRetainedFileCountLimit(string? configuredValue, int defaultValue)
    {
        if (string.IsNullOrWhiteSpace(configuredValue))
        {
            return defaultValue;
        }

        return int.TryParse(configuredValue.Trim(), out var parsed) && parsed > 0
            ? parsed
            : defaultValue;
    }

    public static int ResolveLogRetentionDays(string? configuredValue)
    {
        return int.TryParse(configuredValue?.Trim(), out var parsed) &&
               parsed is > 0 and <= MaximumLogRetentionDays
            ? parsed
            : LogRetentionDays;
    }

    public static void Configure(
        ILoggingBuilder logging,
        string processName,
        string? enabledValue,
        string? directoryValue,
        string? retainedFileCountLimitValue,
        string? runLoggingEnabledValue,
        string? runRetainedFileCountLimitValue,
        string? retentionDaysValue = null)
    {
        if (!IsEnabled(enabledValue))
        {
            return;
        }

        var retainedFileCountLimit = ResolveRetainedFileCountLimit(
            retainedFileCountLimitValue,
            RetainedFileCountLimit);
        var retentionDays = ResolveLogRetentionDays(retentionDaysValue);
        PruneExpiredLogFiles(directoryValue, retentionDays, DateTimeOffset.UtcNow);
        logging.AddProvider(new LocalFileLoggerProvider(processName, directoryValue, retainedFileCountLimit, retentionDays));

        if (!IsEnabled(runLoggingEnabledValue, defaultValue: false))
        {
            return;
        }

        var runRetainedFileCountLimit = ResolveRetainedFileCountLimit(
            runRetainedFileCountLimitValue,
            RunRetainedFileCountLimit);
        logging.AddProvider(new RunScopedFileLoggerProvider(directoryValue, runRetainedFileCountLimit));
    }

    public static string ResolveDirectory(string? configuredDirectory)
    {
        if (!string.IsNullOrWhiteSpace(configuredDirectory))
        {
            return Path.GetFullPath(configuredDirectory);
        }

        return Path.Combine(ResolveDefaultBaseDirectory(), "logs");
    }

    public static string ResolveRollingFilePath(string processName, string? configuredDirectory)
    {
        return Path.Combine(ResolveDirectory(configuredDirectory), $"{processName}-.log");
    }

    public static string ResolveDatedFilePath(string processName, string? configuredDirectory, DateTimeOffset timestamp)
    {
        return Path.Combine(ResolveDirectory(configuredDirectory), $"{processName}-{timestamp:yyyyMMdd}.log");
    }

    public static void PruneDatedFiles(string processName, string? configuredDirectory, int retainedFileCountLimit = RetainedFileCountLimit)
    {
        if (retainedFileCountLimit <= 0)
        {
            return;
        }

        var directory = ResolveDirectory(configuredDirectory);
        if (!Directory.Exists(directory))
        {
            return;
        }

        var files = GetFileInfosFromSafeDirectory(directory, $"{processName}-*.log")
            .OrderByDescending(file => file.LastWriteTimeUtc)
            .Skip(retainedFileCountLimit);

        foreach (var file in files)
        {
            try
            {
                file.Delete();
            }
            catch (IOException exception)
            {
                _ = exception;
            }
            catch (UnauthorizedAccessException exception)
            {
                _ = exception;
            }
            catch (SecurityException exception)
            {
                _ = exception;
            }
        }
    }

    public static void PruneRunLogFiles(string? configuredDirectory, int retainedFileCountLimit = RunRetainedFileCountLimit)
    {
        if (retainedFileCountLimit <= 0)
        {
            return;
        }

        var directory = ResolveRunLogDirectory(configuredDirectory);
        if (!Directory.Exists(directory))
        {
            return;
        }

        var files = GetFileInfosFromSafeDirectory(directory, "*.log")
            .OrderByDescending(file => file.LastWriteTimeUtc)
            .Skip(retainedFileCountLimit);

        foreach (var file in files)
        {
            try
            {
                file.Delete();
            }
            catch (IOException exception)
            {
                _ = exception;
            }
            catch (UnauthorizedAccessException exception)
            {
                _ = exception;
            }
            catch (SecurityException exception)
            {
                _ = exception;
            }
        }
    }

    public static void PruneExpiredLogFiles(
        string? configuredDirectory,
        int retentionDays,
        DateTimeOffset now)
    {
        if (retentionDays is <= 0 or > MaximumLogRetentionDays)
        {
            return;
        }

        var directory = ResolveDirectory(configuredDirectory);
        if (!Directory.Exists(directory))
        {
            return;
        }

        var cutoff = now.UtcDateTime.AddDays(-retentionDays);
        var files = GetFileInfosFromSafeDirectory(directory, "api-*.log")
            .Concat(GetFileInfosFromSafeDirectory(directory, "worker-*.log"))
            .Concat(GetFileInfosFromSafeDirectory(ResolveRunLogDirectory(configuredDirectory), "*.log"))
            .Concat(GetFileInfosFromSafeDirectory(ResolvePreviewLogDirectory(configuredDirectory), "*.jsonl"));

        foreach (var file in files)
        {
            try
            {
                if (file.LastWriteTimeUtc < cutoff)
                {
                    file.Delete();
                }
            }
            catch (IOException exception)
            {
                _ = exception;
            }
            catch (UnauthorizedAccessException exception)
            {
                _ = exception;
            }
            catch (SecurityException exception)
            {
                _ = exception;
            }
        }
    }

    public static string ResolveRunLogDirectory(string? configuredDirectory)
    {
        return Path.Combine(ResolveDirectory(configuredDirectory), "runs");
    }

    public static string ResolvePreviewLogDirectory(string? configuredDirectory)
    {
        return Path.Combine(ResolveDirectory(configuredDirectory), "preview-logs");
    }

    public static string ResolveRunLogPath(string runId, string? configuredDirectory)
    {
        if (string.IsNullOrWhiteSpace(runId))
        {
            throw new ArgumentException("Run ID is required.", nameof(runId));
        }

        return Path.Combine(ResolveRunLogDirectory(configuredDirectory), $"{MakeSafeFileName(runId)}.log");
    }

    private static string MakeSafeFileName(string value)
    {
        var invalidChars = Path.GetInvalidFileNameChars();
        return new string(value.Select(ch => invalidChars.Contains(ch) ? '_' : ch).ToArray());
    }

    private static string[] GetFilesFromSafeDirectory(string directory, string searchPattern)
    {
        try
        {
            var info = new DirectoryInfo(directory);
            if (!info.Exists || info.Attributes.HasFlag(FileAttributes.ReparsePoint))
            {
                return [];
            }

            return Directory.GetFiles(directory, searchPattern, SearchOption.TopDirectoryOnly);
        }
        catch (IOException)
        {
            return [];
        }
        catch (UnauthorizedAccessException)
        {
            return [];
        }
        catch (SecurityException)
        {
            return [];
        }
    }

    private static FileInfo[] GetFileInfosFromSafeDirectory(string directory, string searchPattern)
    {
        var files = new List<FileInfo>();
        foreach (var path in GetFilesFromSafeDirectory(directory, searchPattern))
        {
            try
            {
                var file = new FileInfo(path);
                _ = file.LastWriteTimeUtc;
                files.Add(file);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
            catch (SecurityException)
            {
            }
        }

        return [.. files];
    }

    private static string ResolveDefaultBaseDirectory()
    {
        var repositoryRoot = Environment.GetEnvironmentVariable(RepositoryRootEnvironmentVariable);
        if (!string.IsNullOrWhiteSpace(repositoryRoot))
        {
            return Path.GetFullPath(repositoryRoot);
        }

        var currentDirectory = Path.GetFullPath(Environment.CurrentDirectory);
        var discoveredRepositoryRoot = TryFindRepositoryRoot(currentDirectory);
        if (discoveredRepositoryRoot is null && OperatingSystem.IsWindows())
        {
            var runtimeRoot = SyncFactorsRuntimePaths.TryGetRuntimeRoot();
            if (!string.IsNullOrWhiteSpace(runtimeRoot))
            {
                return runtimeRoot;
            }
        }

        return discoveredRepositoryRoot ?? currentDirectory;
    }

    private static string? TryFindRepositoryRoot(string startDirectory)
    {
        var current = new DirectoryInfo(startDirectory);
        while (current is not null)
        {
            if (File.Exists(Path.Combine(current.FullName, "SyncFactors.Next.sln")) ||
                Directory.Exists(Path.Combine(current.FullName, ".git")))
            {
                return current.FullName;
            }

            current = current.Parent;
        }

        return null;
    }
}

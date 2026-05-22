namespace SyncFactors.Infrastructure;

public static class LocalFileLogging
{
    public const string EnabledEnvironmentVariable = "SYNCFACTORS_LOCAL_FILE_LOGGING_ENABLED";
    public const string DirectoryEnvironmentVariable = "SYNCFACTORS_LOCAL_LOG_DIRECTORY";
    public const string RetainedFileCountLimitEnvironmentVariable = "SYNCFACTORS_LOCAL_LOG_RETAINED_FILE_COUNT";
    public const string RunFileLoggingEnabledEnvironmentVariable = "SYNCFACTORS_RUN_FILE_LOGGING_ENABLED";
    public const string RunRetainedFileCountLimitEnvironmentVariable = "SYNCFACTORS_RUN_LOG_RETAINED_FILE_COUNT";
    public const int RetainedFileCountLimit = 7;
    public const int RunRetainedFileCountLimit = 200;
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

        var files = Directory
            .EnumerateFiles(directory, $"{processName}-*.log", SearchOption.TopDirectoryOnly)
            .Select(path => new FileInfo(path))
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

        var files = Directory
            .EnumerateFiles(directory, "*.log", SearchOption.TopDirectoryOnly)
            .Select(path => new FileInfo(path))
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

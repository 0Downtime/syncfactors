namespace SyncFactors.Infrastructure;

public static class LocalFileLogging
{
    public const string EnabledEnvironmentVariable = "SYNCFACTORS_LOCAL_FILE_LOGGING_ENABLED";
    public const string DirectoryEnvironmentVariable = "SYNCFACTORS_LOCAL_LOG_DIRECTORY";
    private const string RepositoryRootEnvironmentVariable = "REPO_ROOT";

    public static bool IsEnabled(string? configuredValue)
    {
        if (string.IsNullOrWhiteSpace(configuredValue))
        {
            return true;
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

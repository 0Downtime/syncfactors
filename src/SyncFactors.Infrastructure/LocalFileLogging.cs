namespace SyncFactors.Infrastructure;

public static class LocalFileLogging
{
    public const string EnabledEnvironmentVariable = "SYNCFACTORS_LOCAL_FILE_LOGGING_ENABLED";
    public const string DirectoryEnvironmentVariable = "SYNCFACTORS_LOCAL_LOG_DIRECTORY";

    public static bool IsEnabled(string? configuredValue)
    {
        if (string.IsNullOrWhiteSpace(configuredValue))
        {
            return false;
        }

        return configuredValue.Trim().ToLowerInvariant() switch
        {
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

        return Path.Combine(SyncFactorsRuntimePaths.GetRuntimeRoot(), "logs");
    }

    public static string ResolveRollingFilePath(string processName, string? configuredDirectory)
    {
        return Path.Combine(ResolveDirectory(configuredDirectory), $"{processName}-.log");
    }
}

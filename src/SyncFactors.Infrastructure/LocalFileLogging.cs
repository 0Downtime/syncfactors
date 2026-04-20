namespace SyncFactors.Infrastructure;

public static class LocalFileLogging
{
    public const string EnabledEnvironmentVariable = "SYNCFACTORS_LOCAL_FILE_LOGGING_ENABLED";
    public const string DirectoryEnvironmentVariable = "SYNCFACTORS_LOCAL_LOG_DIRECTORY";

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

        return Path.GetFullPath("logs");
    }

    public static string ResolveRollingFilePath(string processName, string? configuredDirectory)
    {
        return Path.Combine(ResolveDirectory(configuredDirectory), $"{processName}-.log");
    }
}

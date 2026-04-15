namespace SyncFactors.Infrastructure;

public static class SyncFactorsRuntimePaths
{
    public static string? TryGetRuntimeRoot()
    {
        if (OperatingSystem.IsWindows())
        {
            var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            if (!string.IsNullOrWhiteSpace(localAppData))
            {
                return Path.Combine(localAppData, "SyncFactors");
            }

            var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            if (!string.IsNullOrWhiteSpace(userProfile))
            {
                return Path.Combine(userProfile, "AppData", "Local", "SyncFactors");
            }
        }
        else
        {
            var xdgDataHome = Environment.GetEnvironmentVariable("XDG_DATA_HOME");
            var basePath = string.IsNullOrWhiteSpace(xdgDataHome)
                ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "share")
                : xdgDataHome;

            return Path.Combine(basePath, "SyncFactors");
        }

        return null;
    }

    public static string GetRuntimeRoot()
    {
        return TryGetRuntimeRoot() ?? Path.Combine(Path.GetFullPath("state"), "SyncFactors");
    }

    public static string GetDefaultSqlitePath()
    {
        return Path.Combine(GetRuntimeRoot(), "state", "syncfactors.db");
    }
}

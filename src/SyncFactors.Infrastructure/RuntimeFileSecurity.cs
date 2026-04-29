namespace SyncFactors.Infrastructure;

internal static class RuntimeFileSecurity
{
    public static void EnsureDirectory(string path)
    {
        Directory.CreateDirectory(path);
        HardenDirectory(path);
    }

    public static void EnsureParentDirectory(string path)
    {
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            EnsureDirectory(directory);
        }
    }

    public static void HardenFile(string path)
    {
        if (!File.Exists(path) || OperatingSystem.IsWindows())
        {
            return;
        }

        try
        {
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
        catch (PlatformNotSupportedException)
        {
        }
    }

    public static void HardenSqliteFiles(string databasePath)
    {
        HardenFile(databasePath);
        HardenFile($"{databasePath}-wal");
        HardenFile($"{databasePath}-shm");
    }

    private static void HardenDirectory(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        try
        {
            File.SetUnixFileMode(
                path,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
        catch (PlatformNotSupportedException)
        {
        }
    }
}

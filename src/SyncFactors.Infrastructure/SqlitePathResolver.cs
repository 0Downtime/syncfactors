namespace SyncFactors.Infrastructure;

public sealed class SqlitePathResolver
{
    private const string DefaultRelativeStatePath = "state/runtime/syncfactors.db";
    private readonly string? _configuredPath;

    public SqlitePathResolver(string? configuredPath = null)
    {
        _configuredPath = configuredPath;
    }

    public string? Resolve()
    {
        if (!string.IsNullOrWhiteSpace(_configuredPath))
        {
            return PathResolution.ResolveExistingFile(_configuredPath);
        }

        return ResolveConfiguredPath();
    }

    public string? ResolveConfiguredPath()
    {
        var configured = string.IsNullOrWhiteSpace(_configuredPath)
            ? GetDefaultRuntimePath()
            : _configuredPath;
        return PathResolution.ResolveConfiguredPath(configured);
    }

    private static string GetDefaultRuntimePath()
    {
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (!string.IsNullOrWhiteSpace(localAppData))
        {
            return Path.Combine(localAppData, "SyncFactors", "state", "syncfactors.db");
        }

        return DefaultRelativeStatePath;
    }
}

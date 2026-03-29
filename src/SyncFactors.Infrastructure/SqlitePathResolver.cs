namespace SyncFactors.Infrastructure;

public sealed class SqlitePathResolver
{
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

        return null;
    }

    public string? ResolveConfiguredPath()
    {
        if (string.IsNullOrWhiteSpace(_configuredPath))
        {
            return null;
        }

        return PathResolution.ResolveConfiguredPath(_configuredPath);
    }
}

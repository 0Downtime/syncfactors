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
            var fullPath = Path.GetFullPath(_configuredPath);
            return File.Exists(fullPath) ? fullPath : null;
        }

        return null;
    }

    public string? ResolveConfiguredPath()
    {
        if (string.IsNullOrWhiteSpace(_configuredPath))
        {
            return null;
        }

        return Path.GetFullPath(_configuredPath);
    }
}

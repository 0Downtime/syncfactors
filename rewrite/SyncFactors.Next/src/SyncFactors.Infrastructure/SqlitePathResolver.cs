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
            return Path.GetFullPath(_configuredPath);
        }

        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null)
        {
            var candidate = Path.Combine(current.FullName, "syncfactors.db");
            if (File.Exists(candidate))
            {
                return candidate;
            }

            current = current.Parent;
        }

        return null;
    }
}

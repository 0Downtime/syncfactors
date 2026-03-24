namespace SyncFactors.Infrastructure;

public sealed class ScaffoldDataPathResolver(string? configuredPath)
{
    public string Resolve()
    {
        return Resolve(
            configuredPath,
            "rewrite/SyncFactors.Next/config/scaffold-data.json");
    }

    private static string Resolve(string? configured, params string[] candidates)
    {
        if (!string.IsNullOrWhiteSpace(configured))
        {
            return Path.GetFullPath(configured);
        }

        foreach (var candidate in candidates)
        {
            var full = Path.GetFullPath(candidate);
            if (File.Exists(full))
            {
                return full;
            }
        }

        return Path.GetFullPath(candidates[0]);
    }
}

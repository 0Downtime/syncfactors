namespace SyncFactors.Infrastructure;

public sealed class SyncFactorsConfigPathResolver(string? configuredConfigPath, string? configuredMappingConfigPath)
{
    public string? ResolveConfigPath()
    {
        return Resolve(
            configuredConfigPath,
            "config/local.real-successfactors.real-ad.sync-config.json",
            "config/local.mock-successfactors.real-ad.sync-config.json");
    }

    public string? ResolveMappingConfigPath()
    {
        return Resolve(
            configuredMappingConfigPath,
            "config/local.syncfactors.mapping-config.json");
    }

    private static string? Resolve(string? configured, params string[] candidates)
    {
        if (!string.IsNullOrWhiteSpace(configured))
        {
            var full = Path.GetFullPath(configured);
            return File.Exists(full) ? full : null;
        }

        foreach (var candidate in candidates)
        {
            var full = Path.GetFullPath(candidate);
            if (File.Exists(full))
            {
                return full;
            }
        }

        return null;
    }
}

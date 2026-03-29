namespace SyncFactors.Infrastructure;

public sealed class SyncFactorsConfigPathResolver(string? configuredConfigPath, string? configuredMappingConfigPath)
{
    public string? ResolveConfigPath()
    {
        return PathResolution.ResolveExistingFile(
            configuredConfigPath,
            "config/local.real-successfactors.real-ad.sync-config.json",
            "config/local.mock-successfactors.real-ad.sync-config.json");
    }

    public string? ResolveMappingConfigPath()
    {
        return PathResolution.ResolveExistingFile(
            configuredMappingConfigPath,
            "config/local.syncfactors.mapping-config.json");
    }
}

namespace SyncFactors.Infrastructure;

public sealed class SyncFactorsConfigPathResolver(string? configuredConfigPath, string? configuredMappingConfigPath)
{
    private const string MockProfile = "mock";
    private const string RealProfile = "real";
    private const string DefaultProfile = MockProfile;
    private const string MockConfigPath = "config/local.mock-successfactors.real-ad.sync-config.json";
    private const string RealConfigPath = "config/local.real-successfactors.real-ad.sync-config.json";
    private const string MappingConfigPath = "config/local.syncfactors.mapping-config.json";

    public string? ResolveConfigPath()
    {
        return PathResolution.ResolveExistingFile(
            ResolveConfiguredConfigPath(),
            ResolveProfileDefaultConfigPath());
    }

    public string? ResolveMappingConfigPath()
    {
        return PathResolution.ResolveExistingFile(
            ResolveConfiguredMappingConfigPath(),
            MappingConfigPath);
    }

    private static string ResolveProfileDefaultConfigPath()
    {
        var profile = Environment.GetEnvironmentVariable("SYNCFACTORS_RUN_PROFILE");
        return string.Equals(profile, RealProfile, StringComparison.OrdinalIgnoreCase)
            ? RealConfigPath
            : MockConfigPath;
    }

    private string? ResolveConfiguredConfigPath()
    {
        if (!string.IsNullOrWhiteSpace(configuredConfigPath))
        {
            return configuredConfigPath;
        }

        var legacyPath = Environment.GetEnvironmentVariable("SYNCFACTORS_CONFIG_PATH");
        if (!string.IsNullOrWhiteSpace(legacyPath))
        {
            return legacyPath;
        }

        var profile = Environment.GetEnvironmentVariable("SYNCFACTORS_RUN_PROFILE");
        if (string.Equals(profile, MockProfile, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(profile, RealProfile, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        return DefaultProfile == RealProfile ? RealConfigPath : MockConfigPath;
    }

    private string? ResolveConfiguredMappingConfigPath()
    {
        if (!string.IsNullOrWhiteSpace(configuredMappingConfigPath))
        {
            return configuredMappingConfigPath;
        }

        var legacyPath = Environment.GetEnvironmentVariable("SYNCFACTORS_MAPPING_CONFIG_PATH");
        return string.IsNullOrWhiteSpace(legacyPath) ? null : legacyPath;
    }
}

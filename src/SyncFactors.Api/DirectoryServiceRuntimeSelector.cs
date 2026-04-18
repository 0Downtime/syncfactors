using SyncFactors.Infrastructure;

namespace SyncFactors.Api;

internal static class DirectoryServiceRuntimeSelector
{
    private const string MockProfile = "mock";

    public static bool UseScaffoldDirectoryServices(SyncFactorsConfigDocument config, string? runProfile)
    {
        _ = config;
        return string.Equals(runProfile, MockProfile, StringComparison.OrdinalIgnoreCase);
    }
}

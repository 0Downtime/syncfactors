namespace SyncFactors.Infrastructure;

public sealed class ScaffoldDataPathResolver(string? configuredPath)
{
    public string Resolve()
    {
        return PathResolution.ResolvePreferredPath(
            configuredPath,
            "config/scaffold-data.json");
    }
}

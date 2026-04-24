namespace SyncFactors.Infrastructure;

public sealed class MockRuntimeFixturePathResolver(string? configuredPath)
{
    public string Resolve()
    {
        if (!string.IsNullOrWhiteSpace(configuredPath))
        {
            return Path.GetFullPath(configuredPath);
        }

        return ResolveRepoRelativePath("state", "runtime", "mock-successfactors.runtime-fixtures.json");
    }

    private static string ResolveRepoRelativePath(params string[] segments)
    {
        var paths = new List<string>
        {
            AppContext.BaseDirectory,
            "..",
            "..",
            "..",
            "..",
            ".."
        };
        paths.AddRange(segments);
        return Path.GetFullPath(Path.Combine(paths.ToArray()));
    }
}

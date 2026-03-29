using SyncFactors.Infrastructure;

namespace SyncFactors.Infrastructure.Tests;

public sealed class PathResolverTests
{
    [Fact]
    public void SyncConfigPathResolver_FindsLocalConfigFromRepoRootRelativeCandidate()
    {
        var configPath = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "config", "local.mock-successfactors.real-ad.sync-config.json"));
        var mappingPath = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "config", "local.syncfactors.mapping-config.json"));
        var originalConfigContents = File.Exists(configPath) ? File.ReadAllText(configPath) : null;
        var originalMappingContents = File.Exists(mappingPath) ? File.ReadAllText(mappingPath) : null;

        Directory.CreateDirectory(Path.GetDirectoryName(configPath)!);
        File.WriteAllText(configPath, "{}");
        File.WriteAllText(mappingPath, "{}");

        try
        {
            var resolver = new SyncFactorsConfigPathResolver(null, null);

            Assert.Equal(configPath, resolver.ResolveConfigPath());
            Assert.Equal(mappingPath, resolver.ResolveMappingConfigPath());
        }
        finally
        {
            if (originalConfigContents is null)
            {
                File.Delete(configPath);
            }
            else
            {
                File.WriteAllText(configPath, originalConfigContents);
            }

            if (originalMappingContents is null)
            {
                File.Delete(mappingPath);
            }
            else
            {
                File.WriteAllText(mappingPath, originalMappingContents);
            }
        }
    }

    [Fact]
    public void ScaffoldDataPathResolver_FindsRepoLevelScaffoldData()
    {
        var scaffoldPath = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "config", "scaffold-data.json"));
        Directory.CreateDirectory(Path.GetDirectoryName(scaffoldPath)!);
        File.WriteAllText(scaffoldPath, "{}");

        try
        {
            var resolver = new ScaffoldDataPathResolver(null);
            Assert.Equal(scaffoldPath, resolver.Resolve());
        }
        finally
        {
            File.Delete(scaffoldPath);
        }
    }

    [Fact]
    public void SqlitePathResolver_ResolvesConfiguredRelativePathAgainstSearchRoots()
    {
        var databasePath = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "state", "runtime", "test-sync.db"));
        Directory.CreateDirectory(Path.GetDirectoryName(databasePath)!);
        File.WriteAllText(databasePath, string.Empty);

        try
        {
            var resolver = new SqlitePathResolver("state/runtime/test-sync.db");

            Assert.Equal(databasePath, resolver.Resolve());
            Assert.Equal(databasePath, resolver.ResolveConfiguredPath());
        }
        finally
        {
            File.Delete(databasePath);
        }
    }
}

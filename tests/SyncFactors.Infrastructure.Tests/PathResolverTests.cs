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
        var originalConfigPath = Environment.GetEnvironmentVariable("SYNCFACTORS_CONFIG_PATH");
        var originalMappingPath = Environment.GetEnvironmentVariable("SYNCFACTORS_MAPPING_CONFIG_PATH");
        var originalProfile = Environment.GetEnvironmentVariable("SYNCFACTORS_RUN_PROFILE");

        Directory.CreateDirectory(Path.GetDirectoryName(configPath)!);
        File.WriteAllText(configPath, "{}");
        File.WriteAllText(mappingPath, "{}");
        Environment.SetEnvironmentVariable("SYNCFACTORS_CONFIG_PATH", null);
        Environment.SetEnvironmentVariable("SYNCFACTORS_MAPPING_CONFIG_PATH", null);
        Environment.SetEnvironmentVariable("SYNCFACTORS_RUN_PROFILE", null);

        try
        {
            var resolver = new SyncFactorsConfigPathResolver(null, null);

            Assert.Equal(configPath, resolver.ResolveConfigPath());
            Assert.Equal(mappingPath, resolver.ResolveMappingConfigPath());
        }
        finally
        {
            Environment.SetEnvironmentVariable("SYNCFACTORS_CONFIG_PATH", originalConfigPath);
            Environment.SetEnvironmentVariable("SYNCFACTORS_MAPPING_CONFIG_PATH", originalMappingPath);
            Environment.SetEnvironmentVariable("SYNCFACTORS_RUN_PROFILE", originalProfile);

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
    public void SyncConfigPathResolver_UsesMockProfileDefaultWhenConfigured()
    {
        var mockConfigPath = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "config", "local.mock-successfactors.real-ad.sync-config.json"));
        var originalContents = File.Exists(mockConfigPath) ? File.ReadAllText(mockConfigPath) : null;
        var originalConfigPath = Environment.GetEnvironmentVariable("SYNCFACTORS_CONFIG_PATH");
        var originalMappingPath = Environment.GetEnvironmentVariable("SYNCFACTORS_MAPPING_CONFIG_PATH");
        var originalProfile = Environment.GetEnvironmentVariable("SYNCFACTORS_RUN_PROFILE");

        Directory.CreateDirectory(Path.GetDirectoryName(mockConfigPath)!);
        File.WriteAllText(mockConfigPath, "{}");
        Environment.SetEnvironmentVariable("SYNCFACTORS_CONFIG_PATH", null);
        Environment.SetEnvironmentVariable("SYNCFACTORS_MAPPING_CONFIG_PATH", null);
        Environment.SetEnvironmentVariable("SYNCFACTORS_RUN_PROFILE", "mock");

        try
        {
            var resolver = new SyncFactorsConfigPathResolver(null, null);
            Assert.Equal(mockConfigPath, resolver.ResolveConfigPath());
        }
        finally
        {
            Environment.SetEnvironmentVariable("SYNCFACTORS_CONFIG_PATH", originalConfigPath);
            Environment.SetEnvironmentVariable("SYNCFACTORS_MAPPING_CONFIG_PATH", originalMappingPath);
            Environment.SetEnvironmentVariable("SYNCFACTORS_RUN_PROFILE", originalProfile);
            RestoreFile(mockConfigPath, originalContents);
        }
    }

    [Fact]
    public void SyncConfigPathResolver_UsesRealProfileDefaultWhenConfigured()
    {
        var realConfigPath = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "config", "local.real-successfactors.real-ad.sync-config.json"));
        var originalContents = File.Exists(realConfigPath) ? File.ReadAllText(realConfigPath) : null;
        var originalConfigPath = Environment.GetEnvironmentVariable("SYNCFACTORS_CONFIG_PATH");
        var originalMappingPath = Environment.GetEnvironmentVariable("SYNCFACTORS_MAPPING_CONFIG_PATH");
        var originalProfile = Environment.GetEnvironmentVariable("SYNCFACTORS_RUN_PROFILE");

        Directory.CreateDirectory(Path.GetDirectoryName(realConfigPath)!);
        File.WriteAllText(realConfigPath, "{}");
        Environment.SetEnvironmentVariable("SYNCFACTORS_CONFIG_PATH", null);
        Environment.SetEnvironmentVariable("SYNCFACTORS_MAPPING_CONFIG_PATH", null);
        Environment.SetEnvironmentVariable("SYNCFACTORS_RUN_PROFILE", "real");

        try
        {
            var resolver = new SyncFactorsConfigPathResolver(null, null);
            Assert.Equal(realConfigPath, resolver.ResolveConfigPath());
        }
        finally
        {
            Environment.SetEnvironmentVariable("SYNCFACTORS_CONFIG_PATH", originalConfigPath);
            Environment.SetEnvironmentVariable("SYNCFACTORS_MAPPING_CONFIG_PATH", originalMappingPath);
            Environment.SetEnvironmentVariable("SYNCFACTORS_RUN_PROFILE", originalProfile);
            RestoreFile(realConfigPath, originalContents);
        }
    }

    [Fact]
    public void SyncConfigPathResolver_PrefersExplicitConfigEnvironmentVariable()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), $"syncfactors-path-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempRoot);
        var explicitConfigPath = Path.Combine(tempRoot, "custom.sync-config.json");
        File.WriteAllText(explicitConfigPath, "{}");
        var originalConfigPath = Environment.GetEnvironmentVariable("SYNCFACTORS_CONFIG_PATH");
        var originalProfile = Environment.GetEnvironmentVariable("SYNCFACTORS_RUN_PROFILE");

        Environment.SetEnvironmentVariable("SYNCFACTORS_CONFIG_PATH", explicitConfigPath);
        Environment.SetEnvironmentVariable("SYNCFACTORS_RUN_PROFILE", "real");

        try
        {
            var resolver = new SyncFactorsConfigPathResolver(null, null);
            Assert.Equal(explicitConfigPath, resolver.ResolveConfigPath());
        }
        finally
        {
            Environment.SetEnvironmentVariable("SYNCFACTORS_CONFIG_PATH", originalConfigPath);
            Environment.SetEnvironmentVariable("SYNCFACTORS_RUN_PROFILE", originalProfile);
            Directory.Delete(tempRoot, recursive: true);
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

    [Fact]
    public void SqlitePathResolver_UsesRuntimeRootDefaultPath_WhenNoPathIsConfigured()
    {
        var resolver = new SqlitePathResolver();

        Assert.Equal(SyncFactorsRuntimePaths.GetDefaultSqlitePath(), resolver.ResolveConfiguredPath());
    }

    [Fact]
    public void LocalFileLogging_ResolvesConfiguredRelativeDirectory_ToFullPath()
    {
        var relativePath = Path.Combine("state", "runtime", "logs");

        Assert.Equal(Path.GetFullPath(relativePath), LocalFileLogging.ResolveDirectory(relativePath));
    }

    [Fact]
    public void LocalFileLogging_DefaultsToRepositoryRootLogsDirectory()
    {
        Assert.Equal(Path.Combine(GetRepositoryRoot(), "logs"), LocalFileLogging.ResolveDirectory(null));
    }

    [Fact]
    public void LocalFileLogging_DefaultsToDiscoveredRepositoryRoot_WhenRunningFromNestedProjectDirectory()
    {
        var originalCurrentDirectory = Environment.CurrentDirectory;
        var originalRepositoryRoot = Environment.GetEnvironmentVariable("REPO_ROOT");
        var tempRoot = Path.Combine(Path.GetTempPath(), "syncfactors-log-root", Guid.NewGuid().ToString("N"));
        var nestedProjectDirectory = Path.Combine(tempRoot, "src", "SyncFactors.Worker");
        Directory.CreateDirectory(nestedProjectDirectory);
        File.WriteAllText(Path.Combine(tempRoot, "SyncFactors.Next.sln"), string.Empty);

        Environment.SetEnvironmentVariable("REPO_ROOT", null);
        Environment.CurrentDirectory = nestedProjectDirectory;
        try
        {
            Assert.Equal(Path.GetFullPath(Path.Combine("..", "..", "logs")), LocalFileLogging.ResolveDirectory(null));
        }
        finally
        {
            Environment.CurrentDirectory = originalCurrentDirectory;
            Environment.SetEnvironmentVariable("REPO_ROOT", originalRepositoryRoot);
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public void LocalFileLogging_UsesRepositoryRootEnvironmentVariable_WhenPresent()
    {
        var originalRepositoryRoot = Environment.GetEnvironmentVariable("REPO_ROOT");
        var tempRoot = Path.Combine(Path.GetTempPath(), "syncfactors-log-root-env", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);

        Environment.SetEnvironmentVariable("REPO_ROOT", tempRoot);
        try
        {
            Assert.Equal(Path.Combine(Path.GetFullPath(tempRoot), "logs"), LocalFileLogging.ResolveDirectory(null));
        }
        finally
        {
            Environment.SetEnvironmentVariable("REPO_ROOT", originalRepositoryRoot);
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    private static string GetRepositoryRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null)
        {
            if (File.Exists(Path.Combine(current.FullName, "SyncFactors.Next.sln")))
            {
                return current.FullName;
            }

            current = current.Parent;
        }

        throw new InvalidOperationException("Could not resolve repository root for the test run.");
    }

    [Theory]
    [InlineData(null, true)]
    [InlineData("", true)]
    [InlineData("0", false)]
    [InlineData("off", false)]
    [InlineData("no", false)]
    [InlineData("false", false)]
    [InlineData("true", true)]
    [InlineData("TRUE", true)]
    [InlineData("1", true)]
    [InlineData("yes", true)]
    [InlineData("on", true)]
    public void LocalFileLogging_ParsesEnabledFlag(string? value, bool expected)
    {
        Assert.Equal(expected, LocalFileLogging.IsEnabled(value));
    }

    [Fact]
    public void SyncFactorsRuntimePaths_UsesXdgDataHome_WhenPresentOnNonWindows()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var originalXdgDataHome = Environment.GetEnvironmentVariable("XDG_DATA_HOME");
        var xdgDataHome = Path.Combine(Path.GetTempPath(), $"syncfactors-xdg-{Guid.NewGuid():N}");
        Environment.SetEnvironmentVariable("XDG_DATA_HOME", xdgDataHome);

        try
        {
            Assert.Equal(
                Path.Combine(xdgDataHome, "SyncFactors"),
                SyncFactorsRuntimePaths.GetRuntimeRoot());
        }
        finally
        {
            Environment.SetEnvironmentVariable("XDG_DATA_HOME", originalXdgDataHome);
        }
    }

    private static void RestoreFile(string path, string? originalContents)
    {
        if (originalContents is null)
        {
            File.Delete(path);
            return;
        }

        File.WriteAllText(path, originalContents);
    }
}

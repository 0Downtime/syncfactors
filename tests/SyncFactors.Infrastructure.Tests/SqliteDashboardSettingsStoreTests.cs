using SyncFactors.Domain;
using SyncFactors.Infrastructure;

namespace SyncFactors.Infrastructure.Tests;

public sealed class SqliteDashboardSettingsStoreTests
{
    [Fact]
    public async Task GetHealthProbesEnabledOverrideAsync_ReturnsNull_WhenUnset()
    {
        var pathResolver = await CreateInitializedDatabaseAsync();
        IDashboardSettingsStore store = new SqliteDashboardSettingsStore(pathResolver);

        var enabledOverride = await store.GetHealthProbesEnabledOverrideAsync(CancellationToken.None);
        var intervalOverride = await store.GetHealthProbeIntervalSecondsOverrideAsync(CancellationToken.None);

        Assert.Null(enabledOverride);
        Assert.Null(intervalOverride);
    }

    [Fact]
    public async Task SaveHealthProbeOverrideAsync_PersistsLatestValues()
    {
        var pathResolver = await CreateInitializedDatabaseAsync();
        IDashboardSettingsStore store = new SqliteDashboardSettingsStore(pathResolver);

        await store.SaveHealthProbeOverrideAsync(true, 75, CancellationToken.None);
        await store.SaveHealthProbeOverrideAsync(false, 90, CancellationToken.None);

        var enabledOverride = await store.GetHealthProbesEnabledOverrideAsync(CancellationToken.None);
        var intervalOverride = await store.GetHealthProbeIntervalSecondsOverrideAsync(CancellationToken.None);

        Assert.False(enabledOverride);
        Assert.Equal(90, intervalOverride);
    }

    private static async Task<SqlitePathResolver> CreateInitializedDatabaseAsync()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "syncfactors-dashboard-settings", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);
        var databasePath = Path.Combine(tempRoot, "runtime.db");
        var pathResolver = new SqlitePathResolver(databasePath);
        var initializer = new SqliteDatabaseInitializer(pathResolver);
        await initializer.InitializeAsync(CancellationToken.None);
        return pathResolver;
    }
}

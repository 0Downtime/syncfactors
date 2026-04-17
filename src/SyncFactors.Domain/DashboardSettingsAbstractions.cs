namespace SyncFactors.Domain;

public interface IDashboardSettingsStore
{
    Task<bool?> GetHealthProbesEnabledOverrideAsync(CancellationToken cancellationToken);
    Task<int?> GetHealthProbeIntervalSecondsOverrideAsync(CancellationToken cancellationToken);
    Task SaveHealthProbeOverrideAsync(bool enabled, int intervalSeconds, CancellationToken cancellationToken);
}

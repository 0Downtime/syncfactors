using SyncFactors.Domain;

namespace SyncFactors.Api;

public sealed record DashboardOptions(
    bool DefaultHealthProbesEnabled,
    int DefaultHealthProbeIntervalSeconds);

public sealed record DashboardHealthProbeState(
    bool Enabled,
    bool IsOverride,
    bool DefaultEnabled,
    int IntervalSeconds,
    int DefaultIntervalSeconds);

public sealed class DashboardSettingsProvider(
    DashboardOptions options,
    IDashboardSettingsStore settingsStore)
{
    public const int MinHealthProbeIntervalSeconds = 15;
    public const int MaxHealthProbeIntervalSeconds = 300;

    public async Task<DashboardHealthProbeState> GetHealthProbeStateAsync(CancellationToken cancellationToken)
    {
        var enabledOverride = await settingsStore.GetHealthProbesEnabledOverrideAsync(cancellationToken);
        var intervalOverride = await settingsStore.GetHealthProbeIntervalSecondsOverrideAsync(cancellationToken);
        return new DashboardHealthProbeState(
            Enabled: enabledOverride ?? options.DefaultHealthProbesEnabled,
            IsOverride: enabledOverride.HasValue || intervalOverride.HasValue,
            DefaultEnabled: options.DefaultHealthProbesEnabled,
            IntervalSeconds: ClampHealthProbeIntervalSeconds(intervalOverride ?? options.DefaultHealthProbeIntervalSeconds),
            DefaultIntervalSeconds: ClampHealthProbeIntervalSeconds(options.DefaultHealthProbeIntervalSeconds));
    }

    public async Task<DashboardHealthProbeState> SetHealthProbesEnabledAsync(bool enabled, CancellationToken cancellationToken)
    {
        var current = await GetHealthProbeStateAsync(cancellationToken);
        await settingsStore.SaveHealthProbeOverrideAsync(enabled, current.IntervalSeconds, cancellationToken);
        return new DashboardHealthProbeState(
            Enabled: enabled,
            IsOverride: true,
            DefaultEnabled: options.DefaultHealthProbesEnabled,
            IntervalSeconds: current.IntervalSeconds,
            DefaultIntervalSeconds: ClampHealthProbeIntervalSeconds(options.DefaultHealthProbeIntervalSeconds));
    }

    public async Task<DashboardHealthProbeState> SetHealthProbeIntervalSecondsAsync(int intervalSeconds, CancellationToken cancellationToken)
    {
        var clampedIntervalSeconds = ClampHealthProbeIntervalSeconds(intervalSeconds);
        var current = await GetHealthProbeStateAsync(cancellationToken);
        await settingsStore.SaveHealthProbeOverrideAsync(current.Enabled, clampedIntervalSeconds, cancellationToken);
        return new DashboardHealthProbeState(
            Enabled: current.Enabled,
            IsOverride: true,
            DefaultEnabled: options.DefaultHealthProbesEnabled,
            IntervalSeconds: clampedIntervalSeconds,
            DefaultIntervalSeconds: ClampHealthProbeIntervalSeconds(options.DefaultHealthProbeIntervalSeconds));
    }

    public static int ClampHealthProbeIntervalSeconds(int intervalSeconds)
    {
        if (intervalSeconds < MinHealthProbeIntervalSeconds)
        {
            return MinHealthProbeIntervalSeconds;
        }

        if (intervalSeconds > MaxHealthProbeIntervalSeconds)
        {
            return MaxHealthProbeIntervalSeconds;
        }

        return intervalSeconds;
    }
}

namespace SyncFactors.Contracts;

public static class DependencyHealthStates
{
    public const string Healthy = "Healthy";
    public const string Degraded = "Degraded";
    public const string Unhealthy = "Unhealthy";
    public const string Unknown = "Unknown";
}

public sealed record DependencyProbeResult(
    string Dependency,
    string Status,
    string Summary,
    string? Details,
    DateTimeOffset CheckedAt,
    long DurationMilliseconds,
    DateTimeOffset? ObservedAt,
    bool IsStale);

public sealed record DependencyHealthSnapshot(
    string Status,
    DateTimeOffset CheckedAt,
    IReadOnlyList<DependencyProbeResult> Probes);

public sealed record WorkerHeartbeat(
    string Service,
    string State,
    string? Activity,
    DateTimeOffset StartedAt,
    DateTimeOffset LastSeenAt);

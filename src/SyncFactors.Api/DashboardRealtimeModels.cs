using SyncFactors.Contracts;

namespace SyncFactors.Api;

public static class DashboardRealtimeEventTypes
{
    public const string DashboardSnapshotUpdated = "dashboardSnapshotUpdated";
    public const string HealthSnapshotUpdated = "healthSnapshotUpdated";
}

public sealed record DashboardRealtimeEvent(
    string Type,
    DateTimeOffset OccurredAt,
    DashboardSnapshot? DashboardSnapshot = null,
    DependencyHealthSnapshot? HealthSnapshot = null);

public interface IDashboardRealtimeClient
{
    Task DashboardEvent(DashboardRealtimeEvent message);
}

public sealed class DashboardRealtimeConnectionTracker
{
    private int _connectionCount;

    public bool HasConnections => Volatile.Read(ref _connectionCount) > 0;

    public void Increment() => Interlocked.Increment(ref _connectionCount);

    public void Decrement()
    {
        var next = Interlocked.Decrement(ref _connectionCount);
        if (next < 0)
        {
            Interlocked.Exchange(ref _connectionCount, 0);
        }
    }
}

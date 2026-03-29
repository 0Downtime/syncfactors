using SyncFactors.Contracts;

namespace SyncFactors.Domain;

public interface IDependencyHealthService
{
    Task<DependencyHealthSnapshot> GetSnapshotAsync(CancellationToken cancellationToken);
}

public interface IWorkerHeartbeatStore
{
    Task<WorkerHeartbeat?> GetCurrentAsync(CancellationToken cancellationToken);
    Task SaveAsync(WorkerHeartbeat heartbeat, CancellationToken cancellationToken);
}

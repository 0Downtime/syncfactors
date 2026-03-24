using SyncFactors.Contracts;

namespace SyncFactors.Domain;

public interface IRuntimeStatusStore
{
    Task<RuntimeStatus?> GetCurrentAsync(CancellationToken cancellationToken);
}

public interface IRunRepository
{
    Task<IReadOnlyList<RunSummary>> ListRunsAsync(CancellationToken cancellationToken);
    Task<RunDetail?> GetRunAsync(string runId, CancellationToken cancellationToken);
    Task<IReadOnlyList<RunEntry>> GetRunEntriesAsync(
        string runId,
        string? bucket,
        string? workerId,
        string? reason,
        string? filter,
        string? entryId,
        CancellationToken cancellationToken);
}

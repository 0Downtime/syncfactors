using SyncFactors.Contracts;

namespace SyncFactors.Domain;

public interface IRuntimeStatusStore
{
    Task<RuntimeStatus?> GetCurrentAsync(CancellationToken cancellationToken);
    Task SaveAsync(RuntimeStatus status, CancellationToken cancellationToken);
}

public interface IRunRepository
{
    Task<IReadOnlyList<RunSummary>> ListRunsAsync(CancellationToken cancellationToken);
    Task<RunDetail?> GetRunAsync(string runId, CancellationToken cancellationToken);
    Task SaveRunAsync(RunRecord run, CancellationToken cancellationToken);
    Task ReplaceRunEntriesAsync(string runId, IReadOnlyList<RunEntryRecord> entries, CancellationToken cancellationToken);
    Task<IReadOnlyList<RunEntry>> GetRunEntriesAsync(
        string runId,
        string? bucket,
        string? workerId,
        string? reason,
        string? filter,
        string? entryId,
        CancellationToken cancellationToken);
}

public interface IWorkerPreviewPlanner
{
    Task<WorkerPreviewResult> PreviewAsync(string workerId, CancellationToken cancellationToken);
}

public interface IWorkerSource
{
    Task<WorkerSnapshot?> GetWorkerAsync(string workerId, CancellationToken cancellationToken);
}

public interface IDirectoryGateway
{
    Task<DirectoryUserSnapshot?> FindByWorkerAsync(WorkerSnapshot worker, CancellationToken cancellationToken);
}

public interface IIdentityMatcher
{
    IdentityMatchResult Match(WorkerSnapshot worker, DirectoryUserSnapshot? directoryUser);
}

public interface IAttributeDiffService
{
    IReadOnlyList<AttributeChange> BuildDiff(WorkerSnapshot worker, DirectoryUserSnapshot? directoryUser);
}

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
    Task<string?> ResolveManagerDistinguishedNameAsync(string managerId, CancellationToken cancellationToken);
    Task<string> ResolveAvailableEmailLocalPartAsync(WorkerSnapshot worker, CancellationToken cancellationToken);
}

public interface IIdentityMatcher
{
    IdentityMatchResult Match(WorkerSnapshot worker, DirectoryUserSnapshot? directoryUser);
}

public interface IAttributeDiffService
{
    Task<IReadOnlyList<AttributeChange>> BuildDiffAsync(
        WorkerSnapshot worker,
        DirectoryUserSnapshot? directoryUser,
        string? proposedEmailAddress,
        string? logPath,
        CancellationToken cancellationToken);
}

public interface IAttributeMappingProvider
{
    IReadOnlyList<AttributeMapping> GetEnabledMappings();
}

public interface IWorkerPreviewLogWriter
{
    string CreateLogPath(string workerId, DateTimeOffset startedAt);
    Task AppendAsync(string logPath, WorkerPreviewLogEntry entry, CancellationToken cancellationToken);
}

public interface IDirectoryCommandGateway
{
    Task<DirectoryCommandResult> ExecuteAsync(DirectoryMutationCommand command, CancellationToken cancellationToken);
}

public sealed record AttributeMapping(
    string Source,
    string Target,
    bool Required,
    string Transform);

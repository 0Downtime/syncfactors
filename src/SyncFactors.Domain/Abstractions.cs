using SyncFactors.Contracts;

namespace SyncFactors.Domain;

public interface IRuntimeStatusStore
{
    Task<RuntimeStatus?> GetCurrentAsync(CancellationToken cancellationToken);
    Task<bool> TryStartAsync(RuntimeStatus status, CancellationToken cancellationToken);
    Task SaveAsync(RuntimeStatus status, CancellationToken cancellationToken);
}

public interface IWorkerPreviewPlanner
{
    Task<WorkerPreviewResult> PreviewAsync(string workerId, CancellationToken cancellationToken);
}

public interface IWorkerSource
{
    Task<WorkerSnapshot?> GetWorkerAsync(string workerId, CancellationToken cancellationToken);
    IAsyncEnumerable<WorkerSnapshot> ListWorkersAsync(WorkerListingMode mode, CancellationToken cancellationToken);
}

public enum WorkerListingMode
{
    Full = 0,
    DeltaPreferred = 1
}

public interface IDeltaSyncService
{
    Task<DeltaSyncWindow> GetWindowAsync(CancellationToken cancellationToken);
    Task RecordSuccessfulRunAsync(CancellationToken cancellationToken);
}

public sealed record DeltaSyncWindow(
    bool Enabled,
    bool HasCheckpoint,
    string? Filter,
    string DeltaField,
    DateTimeOffset? CheckpointUtc,
    DateTimeOffset? EffectiveSinceUtc);
    
public interface IDeltaSyncStateStore
{
    Task<DateTimeOffset?> GetCheckpointAsync(string syncKey, CancellationToken cancellationToken);
    Task SaveCheckpointAsync(string syncKey, DateTimeOffset checkpointUtc, CancellationToken cancellationToken);
}

public interface IDirectoryGateway
{
    Task<DirectoryUserSnapshot?> FindByWorkerAsync(WorkerSnapshot worker, CancellationToken cancellationToken);
    Task<string?> ResolveManagerDistinguishedNameAsync(string managerId, CancellationToken cancellationToken);
    Task<string> ResolveAvailableEmailLocalPartAsync(WorkerSnapshot worker, bool isCreate, CancellationToken cancellationToken);
}

public interface IIdentityMatcher
{
    IdentityMatchResult Match(WorkerSnapshot worker, DirectoryUserSnapshot? directoryUser);
}

public interface ILifecyclePolicy
{
    LifecycleDecision Evaluate(WorkerSnapshot worker, DirectoryUserSnapshot directoryUser);
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

public interface IWorkerPlanningService
{
    Task<PlannedWorkerAction> PlanAsync(WorkerSnapshot worker, string? logPath, CancellationToken cancellationToken);
}

public interface IDirectoryMutationCommandBuilder
{
    DirectoryMutationCommand Build(PlannedWorkerAction plan);
    DirectoryMutationCommand Build(WorkerSnapshot worker, WorkerPreviewResult preview);
}

public interface IRunRepository
{
    Task<IReadOnlyList<RunSummary>> ListRunsAsync(CancellationToken cancellationToken);
    Task<RunDetail?> GetRunAsync(string runId, CancellationToken cancellationToken);
    Task<WorkerPreviewResult?> GetWorkerPreviewAsync(string runId, CancellationToken cancellationToken);
    Task<IReadOnlyList<WorkerPreviewHistoryItem>> ListWorkerPreviewHistoryAsync(string workerId, int take, CancellationToken cancellationToken);
    Task SaveRunAsync(RunRecord run, CancellationToken cancellationToken);
    Task ReplaceRunEntriesAsync(string runId, IReadOnlyList<RunEntryRecord> entries, CancellationToken cancellationToken);
    Task AppendRunEntryAsync(RunEntryRecord entry, CancellationToken cancellationToken);
    Task<IReadOnlyList<RunEntry>> GetRunEntriesAsync(
        string runId,
        string? bucket,
        string? workerId,
        string? reason,
        string? filter,
        string? entryId,
        int skip,
        int take,
        CancellationToken cancellationToken);
    Task<int> CountRunEntriesAsync(
        string runId,
        string? bucket,
        string? workerId,
        string? reason,
        string? filter,
        string? entryId,
        CancellationToken cancellationToken);
}

public interface IDashboardSnapshotService
{
    Task<DashboardSnapshot> GetSnapshotAsync(CancellationToken cancellationToken);
}

public interface IRunQueueStore
{
    Task<RunQueueRequest> EnqueueAsync(StartRunRequest request, CancellationToken cancellationToken);
    Task<RunQueueRequest?> ClaimNextPendingAsync(string workerName, CancellationToken cancellationToken);
    Task<RunQueueRequest?> GetPendingOrActiveAsync(CancellationToken cancellationToken);
    Task<bool> HasPendingOrActiveRunAsync(CancellationToken cancellationToken);
    Task<bool> CancelPendingOrActiveAsync(string? requestedBy, CancellationToken cancellationToken);
    Task<bool> IsCancellationRequestedAsync(string requestId, CancellationToken cancellationToken);
    Task CompleteAsync(string requestId, string runId, CancellationToken cancellationToken);
    Task CancelAsync(string requestId, string? runId, string? errorMessage, CancellationToken cancellationToken);
    Task FailAsync(string requestId, string? runId, string errorMessage, CancellationToken cancellationToken);
}

public interface ISyncScheduleStore
{
    Task<SyncScheduleStatus> GetCurrentAsync(CancellationToken cancellationToken);
    Task<SyncScheduleStatus> UpdateAsync(UpdateSyncScheduleRequest request, CancellationToken cancellationToken);
    Task<SyncScheduleStatus> RecordSuccessfulEnqueueAsync(DateTimeOffset enqueuedAt, CancellationToken cancellationToken);
    Task<SyncScheduleStatus> RecordFailedEnqueueAsync(DateTimeOffset attemptedAt, string errorMessage, CancellationToken cancellationToken);
}

public interface IFullSyncRunService
{
    Task<RunLaunchResult> LaunchAsync(LaunchFullRunRequest request, CancellationToken cancellationToken);
}

public sealed record AttributeMapping(
    string Source,
    string Target,
    bool Required,
    string Transform);

public sealed record LifecycleDecision(
    string Bucket,
    string TargetOu,
    bool TargetEnabled,
    string? Reason);

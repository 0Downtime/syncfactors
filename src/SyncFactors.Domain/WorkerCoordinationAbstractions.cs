using SyncFactors.Contracts;

namespace SyncFactors.Domain;

public interface ISyncScheduleCoordinator
{
    Task<bool> TryEnqueueDueRunAsync(CancellationToken cancellationToken);
}

public interface IGraveyardRetentionReportCoordinator
{
    Task<bool> TrySendDueReportAsync(CancellationToken cancellationToken);
}

public interface IGraveyardAutoDeleteCoordinator
{
    Task<string?> TryExecuteAsync(CancellationToken cancellationToken);
    Task<string> ExecuteApprovedDeleteAsync(RunQueueRequest request, CancellationToken cancellationToken);
}

public interface IBulkRunCoordinator
{
    Task<string> ExecuteAsync(RunQueueRequest request, int maxDegreeOfParallelism, CancellationToken cancellationToken);
}

public interface IDeleteAllUsersCoordinator
{
    Task<string> ExecuteAsync(RunQueueRequest request, CancellationToken cancellationToken);
}

public interface IWorkerExecutionSettings
{
    int GetMaxDegreeOfParallelism();
}

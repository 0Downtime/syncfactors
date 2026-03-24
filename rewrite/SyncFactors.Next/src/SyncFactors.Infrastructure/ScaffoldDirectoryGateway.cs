using SyncFactors.Contracts;
using SyncFactors.Domain;

namespace SyncFactors.Infrastructure;

public sealed class ScaffoldDirectoryGateway(ScaffoldDataStore dataStore) : IDirectoryGateway
{
    public Task<DirectoryUserSnapshot?> FindByWorkerAsync(WorkerSnapshot worker, CancellationToken cancellationToken)
    {
        _ = cancellationToken;

        var directoryUser = dataStore.GetDocument().DirectoryUsers
            .FirstOrDefault(record => string.Equals(record.WorkerId, worker.WorkerId, StringComparison.OrdinalIgnoreCase));

        return Task.FromResult(directoryUser?.ToSnapshot());
    }
}

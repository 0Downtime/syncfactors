using SyncFactors.Contracts;
using SyncFactors.Domain;

namespace SyncFactors.Infrastructure;

public sealed class ScaffoldWorkerSource(ScaffoldDataStore dataStore) : IWorkerSource
{
    public Task<WorkerSnapshot?> GetWorkerAsync(string workerId, CancellationToken cancellationToken)
    {
        _ = cancellationToken;

        if (string.IsNullOrWhiteSpace(workerId))
        {
            return Task.FromResult<WorkerSnapshot?>(null);
        }

        var normalizedId = workerId.Trim();
        var worker = dataStore.GetDocument().Workers
            .FirstOrDefault(record => string.Equals(record.WorkerId, normalizedId, StringComparison.OrdinalIgnoreCase));

        return Task.FromResult(worker?.ToSnapshot());
    }

    public async IAsyncEnumerable<WorkerSnapshot> ListWorkersAsync(WorkerListingMode mode, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        _ = mode;
        foreach (var worker in dataStore.GetDocument().Workers)
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return worker.ToSnapshot();
            await Task.Yield();
        }
    }
}

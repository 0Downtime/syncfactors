using SyncFactors.Contracts;
using SyncFactors.Domain;

namespace SyncFactors.Api;

public sealed class RunEntriesQueryService(IRunRepository runRepository)
{
    public async Task<RunEntriesQueryResult?> LoadAsync(
        string runId,
        string? bucket,
        string? workerId,
        string? reason,
        string? filter,
        string? entryId,
        int? page,
        int? pageSize,
        CancellationToken cancellationToken)
    {
        var run = await runRepository.GetRunAsync(runId, cancellationToken);
        if (run is null)
        {
            return null;
        }

        var resolvedPageSize = Math.Clamp(pageSize ?? 50, 1, 200);
        var total = await runRepository.CountRunEntriesAsync(runId, bucket, workerId, reason, filter, entryId, cancellationToken);
        var totalPages = Math.Max(1, (int)Math.Ceiling(total / (double)resolvedPageSize));
        var resolvedPage = Math.Clamp(page ?? 1, 1, totalPages);
        var attributeTotals = await runRepository.GetRunEntryAttributeTotalsAsync(runId, bucket, workerId, reason, filter, entryId, cancellationToken);
        var entries = await runRepository.GetRunEntriesAsync(
            runId,
            bucket,
            workerId,
            reason,
            filter,
            entryId,
            (resolvedPage - 1) * resolvedPageSize,
            resolvedPageSize,
            cancellationToken);

        return new RunEntriesQueryResult(
            run,
            entries,
            attributeTotals,
            total,
            resolvedPage,
            resolvedPageSize);
    }
}

public sealed record RunEntriesQueryResult(
    RunDetail Run,
    IReadOnlyList<RunEntry> Entries,
    IReadOnlyList<ChangedAttributeTotal> AttributeTotals,
    int Total,
    int Page,
    int PageSize);

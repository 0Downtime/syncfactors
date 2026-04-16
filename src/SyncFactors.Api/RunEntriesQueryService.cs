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
        var employmentStatusTotals = await runRepository.GetRunEntryEmploymentStatusTotalsAsync(runId, bucket, workerId, reason, filter, entryId, cancellationToken);
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
            employmentStatusTotals,
            total,
            resolvedPage,
            resolvedPageSize);
    }

    public async Task<RunEntriesExportResult?> ExportAsync(
        string runId,
        string? bucket,
        string? workerId,
        string? reason,
        string? filter,
        string? entryId,
        CancellationToken cancellationToken)
    {
        var run = await runRepository.GetRunAsync(runId, cancellationToken);
        if (run is null)
        {
            return null;
        }

        var total = await runRepository.CountRunEntriesAsync(runId, bucket, workerId, reason, filter, entryId, cancellationToken);
        var attributeTotals = await runRepository.GetRunEntryAttributeTotalsAsync(runId, bucket, workerId, reason, filter, entryId, cancellationToken);
        var employmentStatusTotals = await runRepository.GetRunEntryEmploymentStatusTotalsAsync(runId, bucket, workerId, reason, filter, entryId, cancellationToken);
        var entries = total == 0
            ? []
            : await runRepository.GetRunEntriesAsync(runId, bucket, workerId, reason, filter, entryId, 0, total, cancellationToken);

        return new RunEntriesExportResult(
            DateTimeOffset.UtcNow,
            new RunEntriesExportFilters(runId, bucket, workerId, reason, filter, entryId),
            new RunEntriesExportRunMetadata(run.Run, run.BucketCounts),
            new RunEntriesExportSummary(total, attributeTotals, employmentStatusTotals),
            entries);
    }
}

public sealed record RunEntriesQueryResult(
    RunDetail Run,
    IReadOnlyList<RunEntry> Entries,
    IReadOnlyList<ChangedAttributeTotal> AttributeTotals,
    IReadOnlyList<EmploymentStatusTotal> EmploymentStatusTotals,
    int Total,
    int Page,
    int PageSize);

public sealed record RunEntriesExportFilters(
    string RunId,
    string? Bucket,
    string? WorkerId,
    string? Reason,
    string? Filter,
    string? EntryId);

public sealed record RunEntriesExportRunMetadata(
    RunSummary Run,
    IReadOnlyDictionary<string, int> BucketCounts);

public sealed record RunEntriesExportSummary(
    int MatchingEntries,
    IReadOnlyList<ChangedAttributeTotal> AttributeTotals,
    IReadOnlyList<EmploymentStatusTotal> EmploymentStatusTotals);

public sealed record RunEntriesExportResult(
    DateTimeOffset ExportedAt,
    RunEntriesExportFilters Filters,
    RunEntriesExportRunMetadata Run,
    RunEntriesExportSummary Summary,
    IReadOnlyList<RunEntry> Entries);

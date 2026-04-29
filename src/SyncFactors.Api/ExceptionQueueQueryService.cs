using SyncFactors.Contracts;
using SyncFactors.Domain;

namespace SyncFactors.Api;

public sealed class ExceptionQueueQueryService(IRunRepository runRepository)
{
    public const int DefaultPageSize = 25;
    public static readonly IReadOnlyList<ExceptionQueueType> QueueTypes =
    [
        new("failedRuns", "Failed Runs", "bad"),
        new("manualReview", "Manual Review", "warn"),
        new("conflicts", "Conflicts", "warn"),
        new("guardrailFailures", "Guardrails", "bad")
    ];

    private static readonly IReadOnlyList<string> EntryQueueTypes = QueueTypes
        .Where(type => !string.Equals(type.Key, "failedRuns", StringComparison.OrdinalIgnoreCase))
        .Select(type => type.Key)
        .ToArray();

    public async Task<ExceptionQueueResult> LoadAsync(
        string? queueType,
        string? search,
        int? page,
        int? pageSize,
        CancellationToken cancellationToken)
    {
        var resolvedQueueType = NormalizeQueueType(queueType);
        var resolvedPageSize = Math.Clamp(pageSize ?? DefaultPageSize, 1, 100);
        var resolvedPage = Math.Max(1, page ?? 1);
        var skipRemaining = (resolvedPage - 1) * resolvedPageSize;
        var items = new List<ExceptionQueueItem>();
        var runs = await runRepository.ListRunsAsync(cancellationToken);
        var summary = BuildSummary(runs);
        var total = 0;

        foreach (var run in runs.OrderByDescending(run => run.StartedAt))
        {
            if (ShouldIncludeFailedRun(resolvedQueueType, run, search))
            {
                total++;
                if (skipRemaining > 0)
                {
                    skipRemaining--;
                }
                else if (items.Count < resolvedPageSize)
                {
                    items.Add(ExceptionQueueItem.ForFailedRun(run));
                }
            }

            foreach (var bucket in ResolveEntryBuckets(resolvedQueueType))
            {
                var count = await runRepository.CountRunEntriesAsync(
                    run.RunId,
                    bucket,
                    workerId: null,
                    reason: null,
                    filter: search,
                    employmentStatus: null,
                    entryId: null,
                    cancellationToken);
                total += count;

                if (items.Count >= resolvedPageSize)
                {
                    continue;
                }

                var localSkip = Math.Min(skipRemaining, count);
                skipRemaining -= localSkip;
                var take = resolvedPageSize - items.Count;
                if (take <= 0 || localSkip >= count)
                {
                    continue;
                }

                var entries = await runRepository.GetRunEntriesAsync(
                    run.RunId,
                    bucket,
                    workerId: null,
                    reason: null,
                    filter: search,
                    employmentStatus: null,
                    entryId: null,
                    skip: localSkip,
                    take: take,
                    cancellationToken);
                items.AddRange(entries.Select(entry => ExceptionQueueItem.ForEntry(run, entry)));
            }
        }

        var totalPages = Math.Max(1, (int)Math.Ceiling(total / (double)resolvedPageSize));
        return new ExceptionQueueResult(
            resolvedQueueType,
            search,
            items,
            summary,
            total,
            resolvedPage,
            resolvedPageSize,
            totalPages);
    }

    private static string? NormalizeQueueType(string? queueType)
    {
        if (string.IsNullOrWhiteSpace(queueType) ||
            string.Equals(queueType, "all", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        return QueueTypes.Any(type => string.Equals(type.Key, queueType, StringComparison.OrdinalIgnoreCase))
            ? queueType
            : null;
    }

    private static IReadOnlyList<string> ResolveEntryBuckets(string? queueType)
    {
        if (string.Equals(queueType, "failedRuns", StringComparison.OrdinalIgnoreCase))
        {
            return [];
        }

        if (!string.IsNullOrWhiteSpace(queueType) && EntryQueueTypes.Contains(queueType, StringComparer.OrdinalIgnoreCase))
        {
            return [queueType];
        }

        return EntryQueueTypes.ToArray();
    }

    private static bool ShouldIncludeFailedRun(string? queueType, RunSummary run, string? search)
    {
        if (!string.IsNullOrWhiteSpace(queueType) &&
            !string.Equals(queueType, "failedRuns", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (!string.Equals(run.Status, "Failed", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (string.IsNullOrWhiteSpace(search))
        {
            return true;
        }

        return Contains(run.RunId, search) ||
            Contains(run.Mode, search) ||
            Contains(run.RunTrigger, search) ||
            Contains(run.RequestedBy, search) ||
            Contains(run.ArtifactType, search);
    }

    private static bool Contains(string? value, string search) =>
        !string.IsNullOrWhiteSpace(value) &&
        value.Contains(search.Trim(), StringComparison.OrdinalIgnoreCase);

    private static IReadOnlyDictionary<string, int> BuildSummary(IReadOnlyList<RunSummary> runs) =>
        new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
        {
            ["failedRuns"] = runs.Count(run => string.Equals(run.Status, "Failed", StringComparison.OrdinalIgnoreCase)),
            ["manualReview"] = runs.Sum(run => run.ManualReview),
            ["conflicts"] = runs.Sum(run => run.Conflicts),
            ["guardrailFailures"] = runs.Sum(run => run.GuardrailFailures)
        };
}

public sealed record ExceptionQueueType(string Key, string Label, string Tone);

public sealed record ExceptionQueueResult(
    string? QueueType,
    string? Search,
    IReadOnlyList<ExceptionQueueItem> Items,
    IReadOnlyDictionary<string, int> Summary,
    int Total,
    int Page,
    int PageSize,
    int TotalPages);

public sealed record ExceptionQueueItem(
    string QueueType,
    string Label,
    string Tone,
    RunSummary Run,
    RunEntry? Entry,
    DateTimeOffset StartedAt,
    string? WorkerId,
    string? SamAccountName,
    string Summary,
    string? Detail)
{
    public static ExceptionQueueItem ForFailedRun(RunSummary run) =>
        new(
            "failedRuns",
            "Failed Run",
            "bad",
            run,
            Entry: null,
            run.StartedAt,
            WorkerId: null,
            SamAccountName: null,
            Summary: $"{UiRunFormatting.DisplayLabel(run.Mode)} failed after processing {run.ProcessedWorkers} of {run.TotalWorkers} workers.",
            Detail: run.RequestedBy is null ? null : $"Requested by {run.RequestedBy}");

    public static ExceptionQueueItem ForEntry(RunSummary run, RunEntry entry) =>
        new(
            entry.Bucket,
            entry.BucketLabel,
            ResolveTone(entry.Bucket),
            run,
            entry,
            entry.StartedAt ?? run.StartedAt,
            entry.WorkerId,
            entry.SamAccountName,
            ResolveSummary(entry),
            ResolveDetail(entry));

    private static string ResolveTone(string bucket) =>
        string.Equals(bucket, "guardrailFailures", StringComparison.OrdinalIgnoreCase) ? "bad" : "warn";

    private static string ResolveSummary(RunEntry entry) =>
        FirstNonEmpty(entry.FailureSummary, entry.PrimarySummary, entry.Reason, entry.ReviewCaseType) ??
        $"{entry.BucketLabel} entry requires operator review.";

    private static string? ResolveDetail(RunEntry entry)
    {
        if (entry.TopChangedAttributes.Count > 0)
        {
            return $"Top changes: {string.Join(", ", entry.TopChangedAttributes)}";
        }

        if (entry.ChangeCount > 0)
        {
            return $"{entry.ChangeCount} changed attributes";
        }

        return entry.ReviewCategory;
    }

    private static string? FirstNonEmpty(params string?[] values) =>
        values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));
}

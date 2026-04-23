namespace SyncFactors.MockSuccessFactors;

public static class MockFixtureSummaryReporter
{
    private static readonly string[] LifecycleOrder =
    [
        MockLifecycleState.Active,
        MockLifecycleState.Preboarding,
        MockLifecycleState.PaidLeave,
        MockLifecycleState.UnpaidLeave,
        MockLifecycleState.Retired,
        MockLifecycleState.Terminated
    ];

    private static readonly string[] ProvisioningBucketOrder =
    [
        "creates",
        "updates",
        "enables",
        "disables",
        "graveyardMoves",
        "deletions",
        "manualReview",
        "quarantined",
        "conflicts",
        "guardrailFailures",
        "unchanged"
    ];

    internal static IReadOnlyList<string> OrderedProvisioningBuckets => ProvisioningBucketOrder;

    public static void WriteSummary(TextWriter output, MockFixtureDocument document, string label)
    {
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(document);

        var lifecycleCounts = document.Workers
            .GroupBy(ResolveLifecycleState, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.Count(), StringComparer.OrdinalIgnoreCase);
        var provisioningBucketCounts = document.Workers
            .GroupBy(InferProvisioningBucket, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.Count(), StringComparer.OrdinalIgnoreCase);
        var scenarioTagCounts = document.Workers
            .SelectMany(worker => worker.ScenarioTags)
            .GroupBy(tag => tag, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.Count(), StringComparer.OrdinalIgnoreCase);

        output.WriteLine($"Mock fixture summary ({label})");
        output.WriteLine($"workers={document.Workers.Count}");
        output.WriteLine($"lifecycleTypes {FormatCounts(lifecycleCounts, LifecycleOrder)}");
        output.WriteLine($"provisioningBuckets {FormatCounts(provisioningBucketCounts, ProvisioningBucketOrder)}");
        output.WriteLine($"scenarioTags {FormatCounts(scenarioTagCounts)}");
    }

    internal static string ResolveLifecycleState(MockWorkerFixture worker)
    {
        return !string.IsNullOrWhiteSpace(worker.LifecycleState)
            ? MockLifecycleState.Normalize(worker.LifecycleState)
            : MockLifecycleState.Infer(worker.StartDate, worker.EmploymentStatus, worker.EndDate, worker.ScenarioTags);
    }

    internal static string InferProvisioningBucket(MockWorkerFixture worker)
    {
        if (HasScenarioTag(worker, "missing-required-attribute"))
        {
            return "manualReview";
        }

        return ResolveLifecycleState(worker) switch
        {
            MockLifecycleState.PaidLeave or MockLifecycleState.UnpaidLeave => "disables",
            MockLifecycleState.Retired or MockLifecycleState.Terminated => "graveyardMoves",
            _ => HasScenarioTag(worker, "create") ? "creates" : "updates"
        };
    }

    internal static string DescribeProvisioningBucket(string bucket)
    {
        return bucket switch
        {
            "creates" => "Create",
            "updates" => "Update",
            "enables" => "Enable",
            "disables" => "Disable",
            "graveyardMoves" => "Move To Graveyard",
            "deletions" => "Delete",
            "manualReview" => "Manual Review",
            "quarantined" => "Quarantined",
            "conflicts" => "Conflict",
            "guardrailFailures" => "Guardrail Failure",
            "unchanged" => "No Change",
            _ => bucket
        };
    }

    private static bool HasScenarioTag(MockWorkerFixture worker, string tag)
    {
        return worker.ScenarioTags.Any(candidate => string.Equals(candidate, tag, StringComparison.OrdinalIgnoreCase));
    }

    private static string FormatCounts(
        IReadOnlyDictionary<string, int> counts,
        IReadOnlyList<string>? preferredOrder = null)
    {
        if (counts.Count == 0)
        {
            return "none";
        }

        var orderedKeys = new List<string>();
        if (preferredOrder is not null)
        {
            foreach (var key in preferredOrder)
            {
                if (counts.ContainsKey(key))
                {
                    orderedKeys.Add(key);
                }
            }
        }

        foreach (var key in counts.Keys.OrderBy(static key => key, StringComparer.OrdinalIgnoreCase))
        {
            if (!orderedKeys.Contains(key, StringComparer.OrdinalIgnoreCase))
            {
                orderedKeys.Add(key);
            }
        }

        return string.Join(
            ", ",
            orderedKeys.Select(key => $"{key}={counts[key]}"));
    }
}

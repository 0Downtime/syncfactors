using SyncFactors.Contracts;

namespace SyncFactors.Domain;

public sealed record RunPopulationTotals(
    int SuccessFactorsActive,
    int ActiveDirectoryEnabled,
    string ActiveOu)
{
    public int Difference => SuccessFactorsActive - ActiveDirectoryEnabled;
}

public static class RunPopulationTotalsBuilder
{
    public static int CountSuccessFactorsActiveWorkers(
        IEnumerable<WorkerSnapshot> workers,
        LifecyclePolicySettings lifecycleSettings)
    {
        var lifecyclePolicy = new LifecyclePolicy(lifecycleSettings);
        var directoryUser = new DirectoryUserSnapshot(
            SamAccountName: null,
            DistinguishedName: null,
            Enabled: null,
            DisplayName: null,
            Attributes: new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase));

        return workers.Count(worker =>
        {
            var decision = lifecyclePolicy.Evaluate(worker, directoryUser);
            return decision.TargetEnabled &&
                   string.Equals(decision.TargetOu, lifecycleSettings.ActiveOu, StringComparison.OrdinalIgnoreCase);
        });
    }

    public static async Task<RunPopulationTotals> BuildAsync(
        IReadOnlyList<WorkerSnapshot> workers,
        IDirectoryGateway directoryGateway,
        LifecyclePolicySettings lifecycleSettings,
        CancellationToken cancellationToken)
    {
        var successFactorsActive = CountSuccessFactorsActiveWorkers(workers, lifecycleSettings);
        var activeUsers = await directoryGateway.ListUsersInOuAsync(lifecycleSettings.ActiveOu, cancellationToken);
        var activeDirectoryEnabled = activeUsers.Count(user => user.Enabled == true);

        return new RunPopulationTotals(
            SuccessFactorsActive: successFactorsActive,
            ActiveDirectoryEnabled: activeDirectoryEnabled,
            ActiveOu: lifecycleSettings.ActiveOu);
    }
}

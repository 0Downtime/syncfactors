using SyncFactors.Contracts;
using SyncFactors.Domain;

namespace SyncFactors.Infrastructure;

public sealed class RuntimeFixtureDirectoryGateway(
    MockRuntimeFixtureReader fixtureReader,
    LifecyclePolicySettings lifecycleSettings,
    TimeProvider timeProvider) : IDirectoryGateway
{
    public Task<DirectoryUserSnapshot?> FindByWorkerAsync(WorkerSnapshot worker, CancellationToken cancellationToken)
    {
        _ = cancellationToken;

        var fixture = fixtureReader.ListWorkers()
            .FirstOrDefault(candidate => MockRuntimeFixtureProjection.MatchesWorkerId(candidate, worker.WorkerId));

        return Task.FromResult(fixture is null ? null : ToSnapshot(fixture));
    }

    public Task<IReadOnlyList<DirectoryUserSnapshot>> ListUsersInOuAsync(string ouDistinguishedName, CancellationToken cancellationToken)
    {
        _ = cancellationToken;

        var snapshots = fixtureReader.ListWorkers()
            .Select(ToSnapshot)
            .Where(user => !string.IsNullOrWhiteSpace(user.DistinguishedName) &&
                           string.Equals(
                               DirectoryDistinguishedName.GetParentOu(user.DistinguishedName),
                               ouDistinguishedName,
                               StringComparison.OrdinalIgnoreCase))
            .ToArray();

        return Task.FromResult<IReadOnlyList<DirectoryUserSnapshot>>(snapshots);
    }

    public Task<string?> ResolveManagerDistinguishedNameAsync(string managerId, CancellationToken cancellationToken)
    {
        _ = cancellationToken;

        var manager = fixtureReader.ListWorkers()
            .FirstOrDefault(candidate => MockRuntimeFixtureProjection.MatchesWorkerId(candidate, managerId));

        return Task.FromResult<string?>(manager is null ? null : MockRuntimeFixtureProjection.BuildDistinguishedName(manager, lifecycleSettings, timeProvider.GetUtcNow()));
    }

    public Task<string> ResolveAvailableEmailLocalPartAsync(WorkerSnapshot worker, bool isCreate, CancellationToken cancellationToken)
    {
        _ = isCreate;
        _ = cancellationToken;
        return Task.FromResult(DirectoryIdentityFormatter.BuildBaseEmailLocalPart(worker.PreferredName, worker.LastName));
    }

    private DirectoryUserSnapshot ToSnapshot(MockRuntimeFixtureWorkerRecord worker)
    {
        var now = timeProvider.GetUtcNow();
        var workerId = MockRuntimeFixtureProjection.ResolveWorkerId(worker);
        var samAccountName = MockRuntimeFixtureProjection.ResolveSamAccountName(worker);
        var displayName = MockRuntimeFixtureProjection.ResolveDisplayName(worker);
        var distinguishedName = MockRuntimeFixtureProjection.BuildDistinguishedName(worker, lifecycleSettings, now);

        var attributes = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
        {
            [lifecycleSettings.DirectoryIdentityAttribute] = workerId,
            ["displayName"] = displayName,
            ["sAMAccountName"] = samAccountName,
            ["personIdExternal"] = worker.PersonIdExternal,
            ["userId"] = worker.UserId,
            ["userName"] = worker.UserName
        };

        return new DirectoryUserSnapshot(
            SamAccountName: string.IsNullOrWhiteSpace(samAccountName) ? null : samAccountName,
            DistinguishedName: distinguishedName,
            Enabled: MockRuntimeFixtureProjection.ResolveEnabled(worker, lifecycleSettings, now),
            DisplayName: displayName,
            Attributes: attributes);
    }
}

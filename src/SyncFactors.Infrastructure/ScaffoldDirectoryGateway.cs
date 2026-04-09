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

    public Task<string> ResolveAvailableEmailLocalPartAsync(WorkerSnapshot worker, bool isCreate, CancellationToken cancellationToken)
    {
        _ = isCreate;
        _ = cancellationToken;
        return Task.FromResult(DirectoryIdentityFormatter.BuildBaseEmailLocalPart(worker.PreferredName, worker.LastName));
    }

    public Task<IReadOnlyList<DirectoryUserSnapshot>> ListUsersInOuAsync(string ouDistinguishedName, CancellationToken cancellationToken)
    {
        _ = cancellationToken;

        var users = dataStore.GetDocument().DirectoryUsers
            .Select(record => record.ToSnapshot())
            .Where(user => !string.IsNullOrWhiteSpace(user.DistinguishedName) &&
                           string.Equals(
                               DirectoryDistinguishedName.GetParentOu(user.DistinguishedName),
                               ouDistinguishedName,
                               StringComparison.OrdinalIgnoreCase))
            .ToArray();

        return Task.FromResult<IReadOnlyList<DirectoryUserSnapshot>>(users);
    }

    public Task<string?> ResolveManagerDistinguishedNameAsync(string managerId, CancellationToken cancellationToken)
    {
        _ = cancellationToken;
        _ = managerId;
        return Task.FromResult<string?>(null);
    }
}

using SyncFactors.Contracts;
using SyncFactors.Domain;

namespace SyncFactors.Infrastructure;

public sealed class ScaffoldDirectoryCommandGateway : IDirectoryCommandGateway
{
    public Task<DirectoryCommandResult> ExecuteAsync(DirectoryMutationCommand command, CancellationToken cancellationToken)
    {
        _ = cancellationToken;

        return Task.FromResult(
            new DirectoryCommandResult(
                Succeeded: true,
                Action: command.Action,
                SamAccountName: command.SamAccountName,
                DistinguishedName: $"CN={command.CommonName},{command.TargetOu}",
                Message: $"Scaffold {command.Action} completed for {command.SamAccountName}.",
                RunId: null));
    }
}

using SyncFactors.Contracts;
using SyncFactors.Domain;
using SyncFactors.Infrastructure;

namespace SyncFactors.Worker;

public sealed class AuditedDirectoryCommandGateway : IDirectoryCommandGateway
{
    private const string Actor = "SyncFactors.Worker";
    private readonly IDirectoryCommandGateway _inner;
    private readonly ISecurityAuditService _audit;

    public AuditedDirectoryCommandGateway(IDirectoryCommandGateway inner, ISecurityAuditService audit)
    {
        _inner = inner;
        _audit = audit;
    }

    public async Task<DirectoryCommandResult> ExecuteAsync(DirectoryMutationCommand command, CancellationToken cancellationToken)
    {
        var correlationId = Guid.NewGuid().ToString("N");
        _audit.Write(
            "MutationIntent",
            "Authorized",
            ("CorrelationId", correlationId),
            ("Actor", Actor),
            ("Action", command.Action),
            ("Target", command.SamAccountName),
            ("WorkerId", command.WorkerId),
            ("TargetOu", command.TargetOu));

        DirectoryCommandResult result;
        try
        {
            result = await _inner.ExecuteAsync(command, cancellationToken);
        }
        catch
        {
            try
            {
                WriteTerminalAudit(command, correlationId, "Failure", null);
            }
            catch
            {
                throw CreateUnknownOutcomeException();
            }

            throw;
        }

        try
        {
            WriteTerminalAudit(command, correlationId, result.Succeeded ? "Success" : "Failure", result.DistinguishedName);
        }
        catch
        {
            throw CreateUnknownOutcomeException();
        }

        return result;
    }

    private void WriteTerminalAudit(DirectoryMutationCommand command, string correlationId, string outcome, string? distinguishedName)
    {
        _audit.Write(
            "DirectoryMutation",
            outcome,
            ("CorrelationId", correlationId),
            ("Actor", Actor),
            ("Action", command.Action),
            ("Target", command.SamAccountName),
            ("WorkerId", command.WorkerId),
            ("TargetOu", command.TargetOu),
            ("DistinguishedName", distinguishedName));
    }

    private static InvalidOperationException CreateUnknownOutcomeException() =>
        new("Directory mutation outcome is unknown because its audit evidence could not be recorded.");
}

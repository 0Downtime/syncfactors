using SyncFactors.Contracts;
using SyncFactors.Domain;

namespace SyncFactors.Infrastructure;

public class AuditedDirectoryCommandGateway : IDirectoryCommandGateway
{
    public const string WorkerActor = "SyncFactors.Worker";
    public const string ApiActor = "SyncFactors.Api";

    private readonly IDirectoryCommandGateway _inner;
    private readonly ISecurityAuditService _audit;
    private readonly string _actor;

    public AuditedDirectoryCommandGateway(
        IDirectoryCommandGateway inner,
        ISecurityAuditService audit,
        string actor = WorkerActor)
    {
        _inner = inner;
        _audit = audit;
        _actor = actor;
    }

    public static IDirectoryCommandGateway Decorate(
        IDirectoryCommandGateway inner,
        ISecurityAuditService audit,
        string actor)
    {
        return inner is IAtomicPreviewDirectoryCommandGateway atomicPreviewGateway
            ? new AuditedAtomicPreviewDirectoryCommandGateway(atomicPreviewGateway, audit, actor)
            : new AuditedDirectoryCommandGateway(inner, audit, actor);
    }

    public Task<DirectoryCommandResult> ExecuteAsync(
        DirectoryMutationCommand command,
        CancellationToken cancellationToken) =>
        ExecuteAuditedAsync(
            command,
            token => _inner.ExecuteAsync(command, token),
            cancellationToken);

    protected async Task<DirectoryCommandResult> ExecuteAuditedAsync(
        DirectoryMutationCommand command,
        Func<CancellationToken, Task<DirectoryCommandResult>> execute,
        CancellationToken cancellationToken)
    {
        var correlationId = Guid.NewGuid().ToString("N");
        _audit.Write(
            "MutationIntent",
            "Authorized",
            ("CorrelationId", correlationId),
            ("Actor", _actor),
            ("Action", command.Action),
            ("Target", command.SamAccountName),
            ("WorkerId", command.WorkerId),
            ("TargetOu", command.TargetOu));

        DirectoryCommandResult result;
        try
        {
            result = await execute(cancellationToken);
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
            WriteTerminalAudit(
                command,
                correlationId,
                result.Succeeded ? "Success" : "Failure",
                result.DistinguishedName);
        }
        catch
        {
            throw CreateUnknownOutcomeException();
        }

        return result;
    }

    private void WriteTerminalAudit(
        DirectoryMutationCommand command,
        string correlationId,
        string outcome,
        string? distinguishedName)
    {
        _audit.Write(
            "DirectoryMutation",
            outcome,
            ("CorrelationId", correlationId),
            ("Actor", _actor),
            ("Action", command.Action),
            ("Target", command.SamAccountName),
            ("WorkerId", command.WorkerId),
            ("TargetOu", command.TargetOu),
            ("DistinguishedName", distinguishedName));
    }

    private static InvalidOperationException CreateUnknownOutcomeException() =>
        new("Directory mutation outcome is unknown because its audit evidence could not be recorded.");
}

internal sealed class AuditedAtomicPreviewDirectoryCommandGateway(
    IAtomicPreviewDirectoryCommandGateway inner,
    ISecurityAuditService audit,
    string actor)
    : AuditedDirectoryCommandGateway(inner, audit, actor), IAtomicPreviewDirectoryCommandGateway
{
    public Task<DirectoryCommandResult> ExecuteIfCurrentAsync(
        DirectoryMutationCommand command,
        WorkerPreviewResult preview,
        CancellationToken cancellationToken) =>
        ExecuteAuditedAsync(
            command,
            token => inner.ExecuteIfCurrentAsync(command, preview, token),
            cancellationToken);
}

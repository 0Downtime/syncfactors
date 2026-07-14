namespace SyncFactors.Api;

public static class ApiAuditWriteHandler
{
    public const string FailureMessage = "The action completed, but security audit recording failed.";

    public static AuditWriteFailureResponse CreateFailureResponse() =>
        new(ActionCompleted: true, AuditRecorded: false, Warning: FailureMessage);

    public static bool TryWrite(Action write)
    {
        try
        {
            write();
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }
}

public sealed record AuditWriteFailureResponse(
    bool ActionCompleted,
    bool AuditRecorded,
    string Warning);

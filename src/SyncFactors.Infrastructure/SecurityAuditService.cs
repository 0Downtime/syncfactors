using Microsoft.Extensions.Logging;

namespace SyncFactors.Infrastructure;

public interface ISecurityAuditService
{
    void Write(string eventType, string outcome, params (string Key, object? Value)[] fields);
}

public sealed class SecurityAuditService(ILogger<SecurityAuditService> logger) : ISecurityAuditService
{
    public void Write(string eventType, string outcome, params (string Key, object? Value)[] fields)
    {
        var values = fields
            .Where(field => field.Value is not null)
            .ToDictionary(field => field.Key, field => field.Value, StringComparer.OrdinalIgnoreCase);

        logger.LogInformation(
            "SecurityAudit EventType={EventType} Outcome={Outcome} Fields={Fields}",
            eventType,
            outcome,
            values);
    }
}

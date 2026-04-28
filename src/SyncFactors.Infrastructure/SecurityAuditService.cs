using Microsoft.Extensions.Logging;
using System.Text.Json;

namespace SyncFactors.Infrastructure;

public interface ISecurityAuditService
{
    void Write(string eventType, string outcome, params (string Key, object? Value)[] fields);
}

public sealed class SecurityAuditService(ILogger<SecurityAuditService> logger) : ISecurityAuditService
{
    private static readonly object FileLock = new();

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

        AppendJsonLine(eventType, outcome, values);
    }

    private static void AppendJsonLine(string eventType, string outcome, IReadOnlyDictionary<string, object?> fields)
    {
        var path = ResolveAuditPath();
        Directory.CreateDirectory(Path.GetDirectoryName(path) ?? ".");
        var entry = new
        {
            timestampUtc = DateTimeOffset.UtcNow,
            eventType,
            outcome,
            fields
        };
        var line = JsonSerializer.Serialize(entry);
        lock (FileLock)
        {
            File.AppendAllText(path, line + Environment.NewLine);
        }
    }

    public static string ResolveAuditPath()
    {
        var configured = Environment.GetEnvironmentVariable("SYNCFACTORS_SECURITY_AUDIT_LOG_PATH");
        if (!string.IsNullOrWhiteSpace(configured))
        {
            return Path.GetFullPath(configured);
        }

        var repoRoot = Environment.GetEnvironmentVariable("REPO_ROOT");
        var root = string.IsNullOrWhiteSpace(repoRoot)
            ? Environment.CurrentDirectory
            : repoRoot;
        return Path.Combine(root, "state", "runtime", "security-audit.jsonl");
    }
}

using Microsoft.Extensions.Logging;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace SyncFactors.Infrastructure;

public interface ISecurityAuditService
{
    void Write(string eventType, string outcome, params (string Key, object? Value)[] fields);
}

public sealed class SecurityAuditService(ILogger<SecurityAuditService> logger) : ISecurityAuditService
{
    private static readonly object FileLock = new();
    public const string IntegrityKeyEnvironmentVariable = "SYNCFACTORS_SECURITY_AUDIT_INTEGRITY_KEY";
    private const string Sha256Algorithm = "SHA256";
    private const string HmacSha256Algorithm = "HMACSHA256";

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
        RuntimeFileSecurity.EnsureParentDirectory(path);
        lock (FileLock)
        {
            var previousHash = ResolvePreviousHash(path);
            var timestampUtc = DateTimeOffset.UtcNow;
            var canonicalFields = CanonicalizeFields(fields);
            var algorithm = ResolveIntegrityAlgorithm();
            var entryHash = ComputeEntryHash(timestampUtc, eventType, outcome, canonicalFields, previousHash, algorithm);
            var entry = new
            {
                timestampUtc,
                eventType,
                outcome,
                fields,
                integrity = new
                {
                    algorithm,
                    previousHash,
                    entryHash
                }
            };
            File.AppendAllText(path, JsonSerializer.Serialize(entry) + Environment.NewLine);
            RuntimeFileSecurity.HardenFile(path);
        }
    }

    public static SecurityAuditIntegrityResult VerifyIntegrity(string path)
    {
        if (!File.Exists(path))
        {
            return new SecurityAuditIntegrityResult(false, 0, "Audit log was not found.");
        }

        string? expectedPreviousHash = null;
        var lineNumber = 0;
        foreach (var line in File.ReadLines(path))
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            lineNumber++;
            using var document = JsonDocument.Parse(line);
            var root = document.RootElement;
            if (!root.TryGetProperty("integrity", out var integrity) ||
                !IsSupportedIntegrityAlgorithm(integrity.TryGetString("algorithm")))
            {
                return new SecurityAuditIntegrityResult(false, lineNumber, "Audit entry is missing supported integrity metadata.");
            }

            var algorithm = integrity.GetRequiredString("algorithm");
            var previousHash = integrity.TryGetString("previousHash");
            if (!string.Equals(previousHash, expectedPreviousHash, StringComparison.Ordinal))
            {
                return new SecurityAuditIntegrityResult(false, lineNumber, "Audit entry previous hash does not match prior entry.");
            }

            var timestampUtc = root.GetProperty("timestampUtc").GetDateTimeOffset();
            var eventType = root.GetRequiredString("eventType");
            var outcome = root.GetRequiredString("outcome");
            var canonicalFields = CanonicalizeFields(root.GetProperty("fields"));
            var expectedEntryHash = ComputeEntryHash(timestampUtc, eventType, outcome, canonicalFields, previousHash, algorithm);
            var actualEntryHash = integrity.TryGetString("entryHash");
            if (!string.Equals(actualEntryHash, expectedEntryHash, StringComparison.Ordinal))
            {
                return new SecurityAuditIntegrityResult(false, lineNumber, "Audit entry hash does not match entry content.");
            }

            expectedPreviousHash = actualEntryHash;
        }

        return new SecurityAuditIntegrityResult(true, lineNumber, null);
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

    private static string? ResolvePreviousHash(string path)
    {
        if (!File.Exists(path))
        {
            return null;
        }

        var lastLine = File.ReadLines(path)
            .LastOrDefault(line => !string.IsNullOrWhiteSpace(line));
        if (string.IsNullOrWhiteSpace(lastLine))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(lastLine);
            if (document.RootElement.TryGetProperty("integrity", out var integrity))
            {
                return integrity.TryGetString("entryHash");
            }
        }
        catch (JsonException)
        {
        }

        return ComputeSha256(lastLine);
    }

    private static string ResolveIntegrityAlgorithm() =>
        string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(IntegrityKeyEnvironmentVariable))
            ? Sha256Algorithm
            : HmacSha256Algorithm;

    private static bool IsSupportedIntegrityAlgorithm(string? algorithm) =>
        string.Equals(algorithm, Sha256Algorithm, StringComparison.Ordinal) ||
        string.Equals(algorithm, HmacSha256Algorithm, StringComparison.Ordinal);

    private static string ComputeEntryHash(
        DateTimeOffset timestampUtc,
        string eventType,
        string outcome,
        string canonicalFields,
        string? previousHash,
        string algorithm)
    {
        var canonicalEntry = string.Join(
            "\n",
            timestampUtc.ToString("O"),
            eventType,
            outcome,
            canonicalFields,
            previousHash ?? string.Empty);
        return string.Equals(algorithm, HmacSha256Algorithm, StringComparison.Ordinal)
            ? ComputeHmacSha256(canonicalEntry)
            : ComputeSha256(canonicalEntry);
    }

    private static string ComputeSha256(string value)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(value));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    private static string ComputeHmacSha256(string value)
    {
        var key = Environment.GetEnvironmentVariable(IntegrityKeyEnvironmentVariable);
        if (string.IsNullOrWhiteSpace(key))
        {
            throw new InvalidOperationException($"Audit log integrity verification requires {IntegrityKeyEnvironmentVariable} for HMACSHA256 entries.");
        }

        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(key));
        var bytes = hmac.ComputeHash(Encoding.UTF8.GetBytes(value));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    private static string CanonicalizeFields(IReadOnlyDictionary<string, object?> fields)
    {
        var ordered = fields
            .OrderBy(field => field.Key, StringComparer.Ordinal)
            .ToDictionary(field => field.Key, field => field.Value, StringComparer.Ordinal);
        return JsonSerializer.Serialize(ordered);
    }

    private static string CanonicalizeFields(JsonElement fields)
    {
        if (fields.ValueKind != JsonValueKind.Object)
        {
            return "{}";
        }

        var parts = fields.EnumerateObject()
            .OrderBy(property => property.Name, StringComparer.Ordinal)
            .Select(property => $"{JsonSerializer.Serialize(property.Name)}:{property.Value.GetRawText()}");
        return "{" + string.Join(",", parts) + "}";
    }
}

public sealed record SecurityAuditIntegrityResult(
    bool IsValid,
    int EntryCount,
    string? Error);

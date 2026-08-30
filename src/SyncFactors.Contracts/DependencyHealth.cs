using System.Security.Cryptography;
using System.Text;

namespace SyncFactors.Contracts;

public static class DependencyHealthStates
{
    public const string Healthy = "Healthy";
    public const string Degraded = "Degraded";
    public const string Unhealthy = "Unhealthy";
    public const string Unknown = "Unknown";
}

public sealed record DependencyProbeResult(
    string Dependency,
    string Status,
    string Summary,
    string? Details,
    DateTimeOffset CheckedAt,
    long DurationMilliseconds,
    DateTimeOffset? ObservedAt,
    bool IsStale);

public sealed record DependencyHealthSnapshot(
    string Status,
    DateTimeOffset CheckedAt,
    IReadOnlyList<DependencyProbeResult> Probes);

public sealed record WorkerHeartbeat(
    string Service,
    string State,
    string? Activity,
    DateTimeOffset StartedAt,
    DateTimeOffset LastSeenAt,
    string? InstanceId = null,
    string? BuildVersion = null,
    string? BuildCommitSha = null,
    string? DeploymentNonceHash = null);

public sealed record WorkerProcessIdentity(
    string InstanceId,
    string BuildVersion,
    string? BuildCommitSha,
    string? DeploymentNonceHash);

public static class WorkerDeploymentAttestation
{
    public const int MinimumNonceLength = 32;
    public const int NonceHashHexLength = 64;

    public static string? HashConfiguredNonce(string? nonce)
    {
        if (string.IsNullOrWhiteSpace(nonce))
        {
            return null;
        }

        var normalized = nonce.Trim();
        if (normalized.Length < MinimumNonceLength)
        {
            throw new InvalidOperationException(
                $"SyncFactors deployment nonce must contain at least {MinimumNonceLength} characters.");
        }

        return HashNormalizedNonce(normalized);
    }

    public static bool MatchesNonce(string? nonce, string? expectedHash)
    {
        if (string.IsNullOrWhiteSpace(nonce) ||
            nonce.Trim().Length < MinimumNonceLength ||
            string.IsNullOrWhiteSpace(expectedHash))
        {
            return false;
        }

        return HashesMatch(expectedHash, HashNormalizedNonce(nonce.Trim()));
    }

    public static bool HashesMatch(string? expectedHash, string? candidateHash)
    {
        if (string.IsNullOrWhiteSpace(expectedHash) ||
            string.IsNullOrWhiteSpace(candidateHash))
        {
            return false;
        }

        var normalizedExpectedHash = expectedHash.Trim();
        var normalizedCandidateHash = candidateHash.Trim();
        if (normalizedExpectedHash.Length != NonceHashHexLength ||
            normalizedCandidateHash.Length != NonceHashHexLength)
        {
            return false;
        }

        try
        {
            var expectedBytes = Convert.FromHexString(normalizedExpectedHash);
            var candidateBytes = Convert.FromHexString(normalizedCandidateHash);
            return expectedBytes.Length == candidateBytes.Length &&
                   CryptographicOperations.FixedTimeEquals(expectedBytes, candidateBytes);
        }
        catch (FormatException)
        {
            return false;
        }
    }

    private static string HashNormalizedNonce(string nonce) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(nonce))).ToLowerInvariant();
}

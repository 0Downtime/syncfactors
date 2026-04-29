using System.Security.Cryptography;
using SyncFactors.Contracts;
using SyncFactors.Domain;

namespace SyncFactors.Infrastructure;

public sealed class RunCaptureMetadataProvider(SyncFactorsConfigPathResolver configPathResolver) : IRunCaptureMetadataProvider
{
    public RunCaptureMetadata Create(string runId, bool dryRun, string syncScope) =>
        new(
            SchemaVersion: 1,
            RunId: runId,
            DryRun: dryRun,
            SyncScope: syncScope,
            SyncConfig: BuildFingerprint(configPathResolver.ResolveConfigPath()),
            MappingConfig: BuildFingerprint(configPathResolver.ResolveMappingConfigPath()));

    private static RunConfigFingerprint? BuildFingerprint(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        var fullPath = Path.GetFullPath(path);
        return new RunConfigFingerprint(
            Path: fullPath,
            Sha256: File.Exists(fullPath) ? ComputeSha256(fullPath) : null);
    }

    private static string ComputeSha256(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }
}

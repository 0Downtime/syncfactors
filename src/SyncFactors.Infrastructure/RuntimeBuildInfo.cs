using System.Reflection;

namespace SyncFactors.Infrastructure;

public sealed record RuntimeBuildInfo(
    string Version,
    string? CommitSha,
    string? ShortCommitSha,
    bool Dirty,
    string AssemblyName)
{
    public static RuntimeBuildInfo FromAssembly(Assembly assembly)
    {
        var informationalVersion = assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
            .InformationalVersion;
        var version = string.IsNullOrWhiteSpace(informationalVersion)
            ? assembly.GetName().Version?.ToString() ?? "unknown"
            : informationalVersion;
        var metadata = assembly.GetCustomAttributes<AssemblyMetadataAttribute>().ToArray();
        var commitSha = metadata.FirstOrDefault(attribute => string.Equals(attribute.Key, "SourceRevisionId", StringComparison.Ordinal))?.Value;
        var dirty = string.Equals(
            metadata.FirstOrDefault(attribute => string.Equals(attribute.Key, "SyncFactorsBuildDirty", StringComparison.Ordinal))?.Value,
            "true",
            StringComparison.OrdinalIgnoreCase) ||
            version.Contains(".dirty", StringComparison.OrdinalIgnoreCase);

        if (string.IsNullOrWhiteSpace(commitSha))
        {
            commitSha = TryParseShortSha(version);
        }

        var shortCommitSha = !string.IsNullOrWhiteSpace(commitSha)
            ? commitSha[..Math.Min(commitSha.Length, 7)].ToLowerInvariant()
            : null;

        return new RuntimeBuildInfo(
            Version: version,
            CommitSha: string.IsNullOrWhiteSpace(commitSha) ? null : commitSha.ToLowerInvariant(),
            ShortCommitSha: shortCommitSha,
            Dirty: dirty,
            AssemblyName: assembly.GetName().Name ?? "unknown");
    }

    private static string? TryParseShortSha(string version)
    {
        const string Marker = "+sha.";
        var markerIndex = version.IndexOf(Marker, StringComparison.OrdinalIgnoreCase);
        if (markerIndex < 0)
        {
            return null;
        }

        var shaStart = markerIndex + Marker.Length;
        var shaLength = 0;
        while (shaStart + shaLength < version.Length && IsHex(version[shaStart + shaLength]))
        {
            shaLength++;
        }

        return shaLength >= 7 ? version.Substring(shaStart, shaLength) : null;
    }

    private static bool IsHex(char value) =>
        value is >= '0' and <= '9' or >= 'a' and <= 'f' or >= 'A' and <= 'F';
}

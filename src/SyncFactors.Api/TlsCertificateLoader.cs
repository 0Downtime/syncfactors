using System.Diagnostics.CodeAnalysis;
using System.Security.Cryptography.X509Certificates;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Configuration;

namespace SyncFactors.Api;

internal static partial class TlsCertificateLoader
{
    private const string MachineCertificateThumbprintKey = "SyncFactors:Tls:MachineCertificateThumbprint";
    private const string MachineCertificateThumbprintEnvironmentVariable = "SYNCFACTORS_TLS_CERT_THUMBPRINT";

    private static readonly string[] ExplicitKestrelCertificateKeys =
    [
        "Kestrel:Certificates:Default:Path",
        "Kestrel:Certificates:Default:KeyPath",
        "Kestrel:Certificates:Default:Subject",
        "Kestrel:Certificates:Default:Thumbprint"
    ];

    [ExcludeFromCodeCoverage(Justification = "LocalMachine certificate store selection requires a Windows host with machine certificate private keys.")]
    public static bool TryLoadDefaultMachineCertificate(
        IConfiguration configuration,
        out X509Certificate2? certificate,
        out string? source)
    {
        certificate = null;
        source = null;

        if (!OperatingSystem.IsWindows() || HasExplicitKestrelCertificate(configuration))
        {
            return false;
        }

        var configuredThumbprint = NormalizeThumbprint(
            configuration[MachineCertificateThumbprintKey] ??
            Environment.GetEnvironmentVariable(MachineCertificateThumbprintEnvironmentVariable));

        using var store = new X509Store(StoreName.My, StoreLocation.LocalMachine);
        store.Open(OpenFlags.ReadOnly);

        if (!string.IsNullOrWhiteSpace(configuredThumbprint))
        {
            certificate = store.Certificates
                .Find(X509FindType.FindByThumbprint, configuredThumbprint, validOnly: false)
                .OfType<X509Certificate2>()
                .Where(IsUsableServerCertificate)
                .OrderByDescending(item => item.NotAfter)
                .FirstOrDefault();
            if (certificate is null)
            {
                return false;
            }

            source = $"LocalMachine\\My thumbprint {certificate.Thumbprint}";
            return true;
        }

        var hostCandidates = GetHostCandidates(configuration);
        if (hostCandidates.Count == 0)
        {
            return false;
        }

        certificate = store.Certificates
            .OfType<X509Certificate2>()
            .Where(IsUsableServerCertificate)
            .Where(item => CertificateMatchesAnyHost(item, hostCandidates))
            .OrderByDescending(item => item.NotAfter)
            .FirstOrDefault();
        if (certificate is null)
        {
            return false;
        }

        source = $"LocalMachine\\My host match {certificate.Subject}";
        return true;
    }

    internal static bool HasExplicitKestrelCertificate(IConfiguration configuration)
    {
        if (ExplicitKestrelCertificateKeys.Any(key => !string.IsNullOrWhiteSpace(configuration[key])))
        {
            return true;
        }

        return !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("ASPNETCORE_Kestrel__Certificates__Default__Path")) ||
               !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("ASPNETCORE_Kestrel__Certificates__Default__KeyPath")) ||
               !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("ASPNETCORE_Kestrel__Certificates__Default__Subject")) ||
               !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("ASPNETCORE_Kestrel__Certificates__Default__Thumbprint"));
    }

    internal static List<string> GetHostCandidates(IConfiguration configuration)
    {
        var candidates = new List<string>();
        var hadPublicHost = AddHostCandidate(candidates, Environment.GetEnvironmentVariable("SYNCFACTORS_API_PUBLIC_HOST"));
        hadPublicHost = AddHostCandidate(candidates, configuration["SyncFactors:ApiPublicHost"]) || hadPublicHost;
        var hasWildcardBinding = false;

        var urls = configuration["urls"] ??
                   configuration["ASPNETCORE_URLS"] ??
                   Environment.GetEnvironmentVariable("ASPNETCORE_URLS");
        if (!string.IsNullOrWhiteSpace(urls))
        {
            foreach (var rawUrl in urls.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                if (Uri.TryCreate(rawUrl, UriKind.Absolute, out var uri))
                {
                    hasWildcardBinding = IsWildcardHost(uri.Host) || hasWildcardBinding;
                    AddHostCandidate(candidates, uri.Host);
                }
            }
        }

        if (hadPublicHost || hasWildcardBinding || candidates.Count == 0)
        {
            AddHostCandidate(candidates, Environment.MachineName);
            AddHostCandidate(candidates, System.Net.Dns.GetHostName());
        }

        return candidates;
    }

    internal static string NormalizeThumbprint(string? thumbprint) =>
        string.IsNullOrWhiteSpace(thumbprint)
            ? string.Empty
            : CertificateThumbprintCharactersRegex().Replace(thumbprint, string.Empty).ToUpperInvariant();

    internal static bool HostMatchesCertificateName(string host, string certificateName)
    {
        if (string.IsNullOrWhiteSpace(host) || string.IsNullOrWhiteSpace(certificateName))
        {
            return false;
        }

        var normalizedHost = host.Trim().TrimEnd('.').ToLowerInvariant();
        var normalizedCertificateName = certificateName.Trim().TrimEnd('.').ToLowerInvariant();
        if (string.Equals(normalizedHost, normalizedCertificateName, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (!normalizedCertificateName.StartsWith("*.", StringComparison.Ordinal))
        {
            return false;
        }

        var suffix = normalizedCertificateName[1..];
        if (!normalizedHost.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var prefix = normalizedHost[..^suffix.Length];
        return prefix.Length > 0 && !prefix.Contains('.', StringComparison.Ordinal);
    }

    internal static bool IsUsableServerCertificate(X509Certificate2 certificate)
    {
        if (!certificate.HasPrivateKey || certificate.NotAfter <= DateTime.Now || certificate.NotBefore > DateTime.Now)
        {
            return false;
        }

        var enhancedKeyUsage = certificate.Extensions.OfType<X509EnhancedKeyUsageExtension>().FirstOrDefault();
        if (enhancedKeyUsage is null)
        {
            return true;
        }

        return enhancedKeyUsage.EnhancedKeyUsages
            .OfType<Oid>()
            .Any(oid => string.Equals(oid.Value, "1.3.6.1.5.5.7.3.1", StringComparison.Ordinal));
    }

    internal static bool CertificateMatchesAnyHost(X509Certificate2 certificate, List<string> hostCandidates)
    {
        var certificateNames = GetCertificateDnsNames(certificate);
        return certificateNames.Any(certificateName =>
            hostCandidates.Any(host => HostMatchesCertificateName(host, certificateName)));
    }

    internal static List<string> GetCertificateDnsNames(X509Certificate2 certificate)
    {
        var names = new List<string>();
        AddHostCandidate(names, certificate.GetNameInfo(X509NameType.DnsName, forIssuer: false));
        AddHostCandidate(names, ExtractCommonName(certificate.Subject));
        return names;
    }

    private static string? ExtractCommonName(string subject)
    {
        if (string.IsNullOrWhiteSpace(subject))
        {
            return null;
        }

        return subject
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(part => part.StartsWith("CN=", StringComparison.OrdinalIgnoreCase))
            .Select(part => part[3..])
            .FirstOrDefault();
    }

    internal static bool AddHostCandidate(List<string> candidates, string? host)
    {
        if (string.IsNullOrWhiteSpace(host))
        {
            return false;
        }

        var normalized = host.Trim().TrimEnd('.').Trim('[', ']');
        if (string.IsNullOrWhiteSpace(normalized) ||
            IsWildcardHost(normalized))
        {
            return false;
        }

        if (!candidates.Contains(normalized, StringComparer.OrdinalIgnoreCase))
        {
            candidates.Add(normalized);
        }

        return true;
    }

    private static bool IsWildcardHost(string host) =>
        string.Equals(host, "*", StringComparison.Ordinal) ||
        string.Equals(host, "+", StringComparison.Ordinal) ||
        string.Equals(host, "0.0.0.0", StringComparison.Ordinal) ||
        string.Equals(host, "::", StringComparison.Ordinal);

    [GeneratedRegex("[^0-9A-Fa-f]", RegexOptions.CultureInvariant)]
    private static partial Regex CertificateThumbprintCharactersRegex();
}

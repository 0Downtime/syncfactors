using System.Diagnostics;
using System.DirectoryServices.Protocols;
using System.Net;
using System.Security.Cryptography.X509Certificates;
using Microsoft.Extensions.Logging;

namespace SyncFactors.Infrastructure;

internal static class ActiveDirectoryConnectionFactory
{
    public static LdapConnection CreateConnection(ActiveDirectoryConfig config, ILogger logger, string purpose, TimeSpan timeout)
    {
        var identifier = new LdapDirectoryIdentifier(config.Server, config.Port ?? GetDefaultPort(config.Transport.Mode));
        var connection = new LdapConnection(identifier)
        {
            AuthType = string.IsNullOrWhiteSpace(config.Username) ? AuthType.Anonymous : AuthType.Basic,
            Timeout = timeout
        };

        if (!string.IsNullOrWhiteSpace(config.Username))
        {
            connection.Credential = new NetworkCredential(config.Username, config.BindPassword);
        }

        connection.SessionOptions.ProtocolVersion = 3;
        connection.SessionOptions.Signing = config.Transport.RequireSigning;
        connection.SessionOptions.Sealing = config.Transport.RequireSigning;
        connection.SessionOptions.VerifyServerCertificate += (_, certificate) => ValidateServerCertificate(certificate, config.Transport);

        if (string.Equals(config.Transport.Mode, "ldaps", StringComparison.OrdinalIgnoreCase))
        {
            connection.SessionOptions.SecureSocketLayer = true;
        }

        var stopwatch = Stopwatch.StartNew();
        logger.LogInformation(
            "Starting AD bind. Purpose={Purpose} Port={Port} Transport={Transport}",
            purpose,
            config.Port ?? GetDefaultPort(config.Transport.Mode),
            config.Transport.Mode);

        if (string.Equals(config.Transport.Mode, "starttls", StringComparison.OrdinalIgnoreCase))
        {
            connection.SessionOptions.StartTransportLayerSecurity(null);
        }

        connection.Bind();
        logger.LogInformation(
            "Completed AD bind. Purpose={Purpose} DurationMs={DurationMs}",
            purpose,
            stopwatch.ElapsedMilliseconds);
        return connection;
    }

    private static bool ValidateServerCertificate(X509Certificate? certificate, ActiveDirectoryTransportConfig transport)
    {
        if (certificate is null)
        {
            return false;
        }

        if (!transport.RequireCertificateValidation)
        {
            return true;
        }

        using var certificate2 = certificate as X509Certificate2 ?? new X509Certificate2(certificate);
        var configuredThumbprints = transport.TrustedCertificateThumbprints
            .Select(NormalizeThumbprint)
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        if (configuredThumbprints.Count > 0)
        {
            var certThumbprint = NormalizeThumbprint(certificate2.Thumbprint);
            return configuredThumbprints.Contains(certThumbprint);
        }

        using var chain = new X509Chain();
        chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
        chain.ChainPolicy.VerificationFlags = X509VerificationFlags.NoFlag;
        return chain.Build(certificate2);
    }

    private static int GetDefaultPort(string mode) =>
        string.Equals(mode, "starttls", StringComparison.OrdinalIgnoreCase) ? 389 : 636;

    private static string NormalizeThumbprint(string? thumbprint) =>
        string.IsNullOrWhiteSpace(thumbprint)
            ? string.Empty
            : thumbprint.Replace(":", string.Empty, StringComparison.Ordinal)
                .Replace(" ", string.Empty, StringComparison.Ordinal)
                .Trim();
}

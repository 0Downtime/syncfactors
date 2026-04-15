using System.Diagnostics;
using System.DirectoryServices.Protocols;
using System.Net;
using System.Security.Cryptography.X509Certificates;
using Microsoft.Extensions.Logging;

namespace SyncFactors.Infrastructure;

internal static class ActiveDirectoryConnectionFactory
{
    public static LdapConnection CreateConnection(ActiveDirectoryConfig config, ILogger logger, TimeSpan timeout)
        => CreateConnectionWithTransport(config, logger, timeout).Connection;

    public static ActiveDirectoryConnectionResult CreateConnectionWithTransport(ActiveDirectoryConfig config, ILogger logger, TimeSpan timeout)
    {
        var primaryMode = config.Transport.Mode;

        try
        {
            return new ActiveDirectoryConnectionResult(
                CreateConnectionForMode(config, logger, timeout, primaryMode),
                RequestedTransport: primaryMode,
                EffectiveTransport: primaryMode,
                UsedFallback: false);
        }
        catch (LdapException primaryException) when (ShouldTryPlainLdapFallback(config, primaryMode))
        {
            var fallbackPort = GetPortForMode(config.Port, "ldap", primaryMode);
            logger.LogWarning(
                primaryException,
                "AD bind failed over {PrimaryTransport}. Retrying with plain LDAP. FallbackPort={Port}",
                primaryMode,
                fallbackPort);

            try
            {
                return new ActiveDirectoryConnectionResult(
                    CreateConnectionForMode(config, logger, timeout, "ldap"),
                    RequestedTransport: primaryMode,
                    EffectiveTransport: "ldap",
                    UsedFallback: true);
            }
            catch (LdapException fallbackException)
            {
                fallbackException.Data["LdapAttemptSummary"] =
                    $"attempted transport='{primaryMode}' on port {GetPortForMode(config.Port, primaryMode, primaryMode)}, then fallback transport='ldap' on port {fallbackPort}";
                throw;
            }
        }
    }

    private static LdapConnection CreateConnectionForMode(ActiveDirectoryConfig config, ILogger logger, TimeSpan timeout, string mode)
    {
        var port = GetPortForMode(config.Port, mode, config.Transport.Mode);
        var identifier = new LdapDirectoryIdentifier(config.Server, port);
        WarnIfSimpleBindUsernameLooksSuspicious(config, logger, mode, port);
        var connection = new LdapConnection(identifier)
        {
            AuthType = string.IsNullOrWhiteSpace(config.Username) ? AuthType.Anonymous : AuthType.Basic,
            Timeout = timeout
        };

        if (!string.IsNullOrWhiteSpace(config.Username))
        {
            connection.Credential = new NetworkCredential(config.Username, config.BindPassword);
        }

        ConfigureSessionOptions(
            value => connection.SessionOptions.ProtocolVersion = value,
            value => connection.SessionOptions.Signing = value,
            value => connection.SessionOptions.Sealing = value,
            config.Transport.RequireSigning);
        connection.SessionOptions.ReferralChasing = ReferralChasingOptions.None;

        if (!string.Equals(mode, "ldap", StringComparison.OrdinalIgnoreCase))
        {
            connection.SessionOptions.VerifyServerCertificate += (_, certificate) => ValidateServerCertificate(certificate, config.Transport);
        }

        if (string.Equals(mode, "ldaps", StringComparison.OrdinalIgnoreCase))
        {
            connection.SessionOptions.SecureSocketLayer = true;
        }

        var stopwatch = Stopwatch.StartNew();
        logger.LogInformation(
            "Starting AD bind. Port={Port} Transport={Transport}",
            port,
            mode);

        if (string.Equals(mode, "starttls", StringComparison.OrdinalIgnoreCase))
        {
            connection.SessionOptions.StartTransportLayerSecurity(null);
        }

        connection.Bind();
        logger.LogInformation(
            "Completed AD bind. DurationMs={DurationMs} Transport={Transport}",
            stopwatch.ElapsedMilliseconds,
            mode);
        return connection;
    }

    private static void WarnIfSimpleBindUsernameLooksSuspicious(ActiveDirectoryConfig config, ILogger logger, string mode, int port)
    {
        if (string.IsNullOrWhiteSpace(config.Username))
        {
            return;
        }

        if (LooksLikeSimpleBindPrincipal(config.Username))
        {
            return;
        }

        logger.LogWarning(
            "[AD-BIND] Active Directory simple bind is using a username that is neither a UPN nor a distinguished name. Username={Username} Port={Port} Transport={Transport}. Prefer a UPN such as user@example.local to avoid inconsistent bind and search behavior.",
            config.Username,
            port,
            mode);
    }

    internal static void ConfigureSessionOptions(
        Action<int> setProtocolVersion,
        Action<bool> setSigning,
        Action<bool> setSealing,
        bool requireSigning)
    {
        setProtocolVersion(3);
        if (!requireSigning)
        {
            return;
        }

        setSigning(true);
        setSealing(true);
    }

    internal static bool LooksLikeSimpleBindPrincipal(string username)
    {
        if (string.IsNullOrWhiteSpace(username))
        {
            return false;
        }

        var trimmed = username.Trim();
        return trimmed.Contains('@', StringComparison.Ordinal) ||
               (trimmed.Contains('=', StringComparison.Ordinal) && trimmed.Contains(',', StringComparison.Ordinal));
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

    private static bool ShouldTryPlainLdapFallback(ActiveDirectoryConfig config, string mode) =>
        config.Transport.AllowLdapFallback &&
        !string.Equals(mode, "ldap", StringComparison.OrdinalIgnoreCase);

    private static int GetPortForMode(int? configuredPort, string requestedMode, string configuredMode)
    {
        if (configuredPort is null)
        {
            return GetDefaultPort(requestedMode);
        }

        if (string.Equals(requestedMode, "ldap", StringComparison.OrdinalIgnoreCase) &&
            configuredPort.Value == GetDefaultPort(configuredMode))
        {
            return GetDefaultPort("ldap");
        }

        return configuredPort.Value;
    }

    private static int GetDefaultPort(string mode) =>
        string.Equals(mode, "ldaps", StringComparison.OrdinalIgnoreCase) ? 636 : 389;

    private static string NormalizeThumbprint(string? thumbprint) =>
        string.IsNullOrWhiteSpace(thumbprint)
            ? string.Empty
            : thumbprint.Replace(":", string.Empty, StringComparison.Ordinal)
                .Replace(" ", string.Empty, StringComparison.Ordinal)
                .Trim();
}

internal sealed record ActiveDirectoryConnectionResult(
    LdapConnection Connection,
    string RequestedTransport,
    string EffectiveTransport,
    bool UsedFallback);

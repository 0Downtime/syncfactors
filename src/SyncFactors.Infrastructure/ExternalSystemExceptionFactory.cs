using System.DirectoryServices.Protocols;

namespace SyncFactors.Infrastructure;

internal static class ExternalSystemExceptionFactory
{
    public static InvalidOperationException CreateActiveDirectoryException(string operation, ActiveDirectoryConfig config, Exception exception)
        => CreateActiveDirectoryException(operation, config, exception, details: null);

    public static InvalidOperationException CreateActiveDirectoryException(string operation, ActiveDirectoryConfig config, Exception exception, string? details)
    {
        var server = config.Server;
        var summary = exception switch
        {
            LdapException ldapException => DescribeLdapFailure(ldapException, config),
            DirectoryOperationException directoryOperationException => directoryOperationException.Message,
            _ => exception.Message
        };
        var guidance = exception switch
        {
            LdapException ldapException => GetActiveDirectoryGuidance(ldapException, config),
            DirectoryOperationException => "Check the target OU, manager resolution, and whether the account already exists with unexpected state.",
            _ => "Verify the configured LDAP server, bind account, and network reachability from this machine."
        };

        return new InvalidOperationException(
            $"Active Directory {operation} failed against LDAP server '{server}'. {summary}{FormatDetails(details)} Next check: {guidance}",
            exception);
    }

    public static InvalidOperationException CreateActiveDirectoryTimeoutException(string operation, string server, TimeSpan timeout, Exception? innerException = null)
    {
        return new InvalidOperationException(
            $"Active Directory {operation} timed out against LDAP server '{server}' after {timeout.TotalSeconds:0} seconds.",
            innerException);
    }

    public static InvalidOperationException CreateSuccessFactorsException(string operation, string endpoint, string summary, Exception? innerException = null)
    {
        var guidance = GetSuccessFactorsGuidance(summary);
        return new InvalidOperationException(
            $"SuccessFactors {operation} failed for endpoint '{endpoint}'. {summary} Next check: {guidance}",
            innerException);
    }

    private static string DescribeLdapFailure(LdapException exception, ActiveDirectoryConfig config)
    {
        if (string.Equals(exception.Message, "The supplied credential is invalid.", StringComparison.OrdinalIgnoreCase))
        {
            return "The configured bind credentials were rejected by the directory server.";
        }

        if (string.Equals(exception.Message, "The LDAP server is unavailable.", StringComparison.OrdinalIgnoreCase))
        {
            return $"The LDAP server could not be reached. Connection settings: host='{config.Server}', port={config.Port ?? GetDefaultPort(config.Transport.Mode)}, transport={config.Transport.Mode}.";
        }

        if (exception.ServerErrorMessage?.Contains("stronger", StringComparison.OrdinalIgnoreCase) == true ||
            exception.ServerErrorMessage?.Contains("signing", StringComparison.OrdinalIgnoreCase) == true)
        {
            return "The directory rejected the connection because LDAP signing or a stronger transport is required.";
        }

        return exception.Message;
    }

    private static string GetActiveDirectoryGuidance(LdapException exception, ActiveDirectoryConfig config)
    {
        if (string.Equals(exception.Message, "The supplied credential is invalid.", StringComparison.OrdinalIgnoreCase))
        {
            return "Confirm the bind username format and password. For simple bind, prefer a UPN such as user@example.local.";
        }

        if (string.Equals(exception.Message, "The LDAP server is unavailable.", StringComparison.OrdinalIgnoreCase))
        {
            var defaultPort = GetDefaultPort(config.Transport.Mode);
            var configuredPort = config.Port ?? defaultPort;
            var hostPortHint = LooksLikeHostAndPort(config.Server)
                ? " The configured AD server appears to include a port in the host field; set the host in SF_AD_SYNC_AD_SERVER/ad.server and set the port separately in ad.port."
                : string.Empty;
            return $"Confirm the server name, port, VPN/network path, and whether LDAPS or LDAP signing is required. Current config: host='{config.Server}', port={configuredPort}, transport={config.Transport.Mode}.{hostPortHint}";
        }

        if (exception.ServerErrorMessage?.Contains("stronger", StringComparison.OrdinalIgnoreCase) == true ||
            exception.ServerErrorMessage?.Contains("signing", StringComparison.OrdinalIgnoreCase) == true)
        {
            return "Switch the directory connection to the required secure transport before retrying.";
        }

        return "Review the LDAP error detail, target OU, and manager lookup inputs before retrying.";
    }

    private static string GetSuccessFactorsGuidance(string summary)
    {
        if (summary.Contains("Status=401", StringComparison.OrdinalIgnoreCase) ||
            summary.Contains("Status=403", StringComparison.OrdinalIgnoreCase))
        {
            return "Confirm the configured SuccessFactors credentials, OAuth client, and company context.";
        }

        if (summary.Contains("invalid JSON", StringComparison.OrdinalIgnoreCase))
        {
            return "Check whether the endpoint or authentication flow returned HTML or an intermediary error page instead of OData JSON.";
        }

        if (summary.Contains("invalid property", StringComparison.OrdinalIgnoreCase) ||
            summary.Contains("BadRequest", StringComparison.OrdinalIgnoreCase))
        {
            return "Compare the configured select/expand paths with tenant metadata and remove unsupported fields.";
        }

        if (summary.Contains("No worker was returned", StringComparison.OrdinalIgnoreCase))
        {
            return "Confirm the worker ID and preview query filters against the tenant data.";
        }

        return "Confirm the endpoint URL, authentication settings, and selected field paths for this tenant.";
    }

    private static string FormatDetails(string? details)
    {
        return string.IsNullOrWhiteSpace(details)
            ? " "
            : $" Details: {details} ";
    }

    private static int GetDefaultPort(string mode) =>
        string.Equals(mode, "starttls", StringComparison.OrdinalIgnoreCase) ? 389 : 636;

    private static bool LooksLikeHostAndPort(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var trimmed = value.Trim();
        if (trimmed.StartsWith("[", StringComparison.Ordinal) && trimmed.Contains("]:", StringComparison.Ordinal))
        {
            return true;
        }

        var colonCount = trimmed.Count(character => character == ':');
        return colonCount == 1;
    }
}

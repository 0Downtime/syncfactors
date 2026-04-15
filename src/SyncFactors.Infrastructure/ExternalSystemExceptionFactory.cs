using System.DirectoryServices.Protocols;

namespace SyncFactors.Infrastructure;

internal static class ExternalSystemExceptionFactory
{
    public static InvalidOperationException CreateActiveDirectoryValidationException(string operation, ActiveDirectoryConfig config, string summary, string? details, string guidance)
    {
        var server = config.Server;
        return new InvalidOperationException(
            $"Active Directory {operation} failed against LDAP server '{server}'. {summary}{FormatDetails(details)} Next check: {guidance}");
    }

    public static InvalidOperationException CreateActiveDirectoryException(string operation, ActiveDirectoryConfig config, Exception exception)
        => CreateActiveDirectoryException(operation, config, exception, details: null);

    public static InvalidOperationException CreateActiveDirectoryException(string operation, ActiveDirectoryConfig config, Exception exception, string? details)
    {
        var server = config.Server;
        var summary = exception switch
        {
            LdapException ldapException => DescribeLdapFailure(ldapException, config),
            DirectoryOperationException directoryOperationException => DescribeDirectoryOperationFailure(directoryOperationException, config),
            _ => exception.Message
        };
        var guidance = exception switch
        {
            LdapException ldapException => GetActiveDirectoryGuidance(ldapException, config),
            DirectoryOperationException directoryOperationException => GetDirectoryOperationGuidance(directoryOperationException, config),
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
        if (TryDescribeMissingOu(exception.Message, out var missingOuDescription))
        {
            return AppendLdapServerDiagnostics(missingOuDescription, exception);
        }

        if (string.Equals(exception.Message, "The supplied credential is invalid.", StringComparison.OrdinalIgnoreCase))
        {
            return AppendLdapServerDiagnostics("The configured bind credentials were rejected by the directory server.", exception);
        }

        if (string.Equals(exception.Message, "The LDAP server is unavailable.", StringComparison.OrdinalIgnoreCase))
        {
            return AppendLdapServerDiagnostics(
                $"The LDAP server could not be reached. Connection settings: host='{config.Server}', port={config.Port ?? GetDefaultPort(config.Transport.Mode)}, transport={config.Transport.Mode}.{FormatAttemptSummary(exception)}",
                exception);
        }

        if (exception.ServerErrorMessage?.Contains("stronger", StringComparison.OrdinalIgnoreCase) == true ||
            exception.ServerErrorMessage?.Contains("signing", StringComparison.OrdinalIgnoreCase) == true)
        {
            return AppendLdapServerDiagnostics("The directory rejected the connection because LDAP signing or a stronger transport is required.", exception);
        }

        return AppendLdapServerDiagnostics(exception.Message, exception);
    }

    private static string DescribeDirectoryOperationFailure(DirectoryOperationException exception, ActiveDirectoryConfig config)
    {
        _ = config;

        if (TryDescribeMissingOu(exception.Message, out var missingOuDescription))
        {
            return missingOuDescription;
        }

        return exception.Message;
    }

    private static string GetActiveDirectoryGuidance(LdapException exception, ActiveDirectoryConfig config)
    {
        if (TryDescribeMissingOu(exception.Message, out _))
        {
            return GetMissingOuGuidance(config);
        }

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
            return $"Confirm the server name, port, VPN/network path, and whether LDAPS or LDAP signing is required. Current config: host='{config.Server}', port={configuredPort}, transport={config.Transport.Mode}.{FormatAttemptSummary(exception)}{hostPortHint}";
        }

        if (exception.ServerErrorMessage?.Contains("stronger", StringComparison.OrdinalIgnoreCase) == true ||
            exception.ServerErrorMessage?.Contains("signing", StringComparison.OrdinalIgnoreCase) == true)
        {
            return "Switch the directory connection to the required secure transport before retrying.";
        }

        return "Review the LDAP error detail, target OU, and manager lookup inputs before retrying.";
    }

    private static string GetDirectoryOperationGuidance(DirectoryOperationException exception, ActiveDirectoryConfig config)
    {
        if (TryDescribeMissingOu(exception.Message, out _))
        {
            return GetMissingOuGuidance(config);
        }

        return "Check the target OU, manager resolution, and whether the account already exists with unexpected state.";
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

    private static string FormatAttemptSummary(LdapException exception)
    {
        return exception.Data["LdapAttemptSummary"] is string attemptSummary && !string.IsNullOrWhiteSpace(attemptSummary)
            ? $" Attempt summary: {attemptSummary}."
            : string.Empty;
    }

    private static string AppendLdapServerDiagnostics(string summary, LdapException exception)
    {
        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(summary))
        {
            parts.Add(summary.Trim());
        }

        if (exception.ErrorCode != 0)
        {
            parts.Add($"LDAP error code {exception.ErrorCode}.");
        }

        var serverDetail = exception.ServerErrorMessage?.Trim();
        if (!string.IsNullOrWhiteSpace(serverDetail) &&
            !summary.Contains(serverDetail, StringComparison.OrdinalIgnoreCase))
        {
            parts.Add($"Server detail: {serverDetail}");
        }

        return string.Join(" ", parts);
    }

    private static int GetDefaultPort(string mode) =>
        string.Equals(mode, "ldaps", StringComparison.OrdinalIgnoreCase) ? 636 : 389;

    private static bool TryDescribeMissingOu(string? message, out string description)
    {
        if (!string.IsNullOrWhiteSpace(message) &&
            message.Contains("problem 2001 (NO_OBJECT)", StringComparison.OrdinalIgnoreCase))
        {
            var bestMatchMarker = "best match of:";
            var bestMatchIndex = message.IndexOf(bestMatchMarker, StringComparison.OrdinalIgnoreCase);
            if (bestMatchIndex >= 0)
            {
                var bestMatch = message[(bestMatchIndex + bestMatchMarker.Length)..]
                    .Trim()
                    .Trim('\'', '"');
                description = $"The directory search base does not exist or is misconfigured. Best match in AD was '{bestMatch}'.";
                return true;
            }

            description = "The directory search base does not exist or is misconfigured.";
            return true;
        }

        description = string.Empty;
        return false;
    }

    private static string GetMissingOuGuidance(ActiveDirectoryConfig config)
    {
        var configuredOus = new[]
        {
            $"defaultActiveOu='{config.DefaultActiveOu}'",
            $"prehireOu='{config.PrehireOu}'",
            $"graveyardOu='{config.GraveyardOu}'",
            string.IsNullOrWhiteSpace(config.LeaveOu) ? null : $"leaveOu='{config.LeaveOu}'"
        }
        .Where(value => !string.IsNullOrWhiteSpace(value));

        return $"Check that each configured AD OU exists exactly as specified and that the bind account can search it. Current OUs: {string.Join(", ", configuredOus)}.";
    }

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

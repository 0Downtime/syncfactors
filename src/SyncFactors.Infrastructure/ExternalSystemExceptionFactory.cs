using System.DirectoryServices.Protocols;

namespace SyncFactors.Infrastructure;

internal static class ExternalSystemExceptionFactory
{
    internal const string RetryableActiveDirectoryTimeoutMarkerKey = "SyncFactors.Infrastructure.RetryableActiveDirectoryTimeout";
    private const string PasswordRestrictionErrorCode = "0000052D";

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
            LdapException ldapException => DescribeLdapFailure(operation, ldapException, config, details),
            DirectoryOperationException directoryOperationException => DescribeDirectoryOperationFailure(operation, directoryOperationException, config, details),
            _ => exception.Message
        };
        var guidance = exception switch
        {
            LdapException ldapException => GetActiveDirectoryGuidance(operation, ldapException, config, details),
            DirectoryOperationException directoryOperationException => GetDirectoryOperationGuidance(operation, directoryOperationException, config, details),
            _ => "Verify the configured LDAP server, bind account, and network reachability from this machine."
        };

        return new InvalidOperationException(
            $"Active Directory {operation} failed against LDAP server '{server}'. {summary}{FormatDetails(details)} Next check: {guidance}",
            exception);
    }

    public static InvalidOperationException CreateActiveDirectoryTimeoutException(string operation, string server, TimeSpan timeout, Exception? innerException = null)
    {
        var exception = new InvalidOperationException(
            $"Active Directory {operation} timed out against LDAP server '{server}' after {timeout.TotalSeconds:0} seconds.",
            innerException);
        exception.Data[RetryableActiveDirectoryTimeoutMarkerKey] = true;
        return exception;
    }

    public static InvalidOperationException CreateSuccessFactorsException(string operation, string endpoint, string summary, Exception? innerException = null)
    {
        var guidance = GetSuccessFactorsGuidance(summary);
        return new InvalidOperationException(
            $"SuccessFactors {operation} failed for endpoint '{endpoint}'. {summary} Next check: {guidance}",
            innerException);
    }

    private static string DescribeLdapFailure(string operation, LdapException exception, ActiveDirectoryConfig config, string? details)
    {
        if (TryDescribeMissingOu(operation, exception.Message, details, out var missingOuDescription))
        {
            return AppendRawActiveDirectoryMessage(
                AppendLdapServerDiagnostics(missingOuDescription, exception),
                exception.Message);
        }

        if (IsPasswordRestrictionFailure(exception))
        {
            return AppendRawActiveDirectoryMessage(
                "The directory rejected the request because the account needs a compliant password before it can be enabled.",
                exception.ServerErrorMessage ?? exception.Message);
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

    private static string DescribeDirectoryOperationFailure(string operation, DirectoryOperationException exception, ActiveDirectoryConfig config, string? details)
    {
        _ = config;

        if (TryDescribeMissingOu(operation, exception.Message, details, out var missingOuDescription))
        {
            return AppendRawActiveDirectoryMessage(missingOuDescription, exception.Message);
        }

        if (IsPasswordRestrictionFailure(exception))
        {
            return AppendRawActiveDirectoryMessage(
                "The directory rejected the request because the account needs a compliant password before it can be enabled.",
                exception.Message);
        }

        return exception.Message;
    }

    private static string GetActiveDirectoryGuidance(string operation, LdapException exception, ActiveDirectoryConfig config, string? details)
    {
        if (TryDescribeMissingOu(operation, exception.Message, details, out _))
        {
            return GetMissingOuGuidance(operation, config, details);
        }

        if (IsPasswordRestrictionFailure(exception))
        {
            return GetPasswordRestrictionGuidance(config);
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

    private static string GetDirectoryOperationGuidance(string operation, DirectoryOperationException exception, ActiveDirectoryConfig config, string? details)
    {
        if (TryDescribeMissingOu(operation, exception.Message, details, out _))
        {
            return GetMissingOuGuidance(operation, config, details);
        }

        if (IsPasswordRestrictionFailure(exception))
        {
            return GetPasswordRestrictionGuidance(config);
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

    private static bool IsPasswordRestrictionFailure(LdapException exception)
    {
        return ContainsPasswordRestrictionMarker(exception.Message) ||
               ContainsPasswordRestrictionMarker(exception.ServerErrorMessage);
    }

    private static bool IsPasswordRestrictionFailure(DirectoryOperationException exception)
    {
        return ContainsPasswordRestrictionMarker(exception.Message);
    }

    private static bool ContainsPasswordRestrictionMarker(string? value)
    {
        return !string.IsNullOrWhiteSpace(value) &&
               value.Contains(PasswordRestrictionErrorCode, StringComparison.OrdinalIgnoreCase);
    }

    private static string GetPasswordRestrictionGuidance(ActiveDirectoryConfig config)
    {
        return SupportsPasswordProvisioningTransport(config.Transport.Mode)
            ? "Reset the account password to a compliant value, or retry over LDAPS/StartTLS so SyncFactors can set one automatically before enabling."
            : "Switch AD transport to LDAPS or StartTLS so SyncFactors can set a compliant password automatically, or reset the account password manually before enabling.";
    }

    internal static bool IsRetryableActiveDirectoryTimeout(Exception exception)
    {
        return exception.Data[RetryableActiveDirectoryTimeoutMarkerKey] as bool? == true;
    }

    private static int GetDefaultPort(string mode) =>
        string.Equals(mode, "ldaps", StringComparison.OrdinalIgnoreCase) ? 636 : 389;

    private static bool SupportsPasswordProvisioningTransport(string mode)
    {
        return string.Equals(mode, "ldaps", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(mode, "starttls", StringComparison.OrdinalIgnoreCase);
    }

    private static bool TryDescribeMissingOu(string operation, string? message, string? details, out string description)
    {
        if (!string.IsNullOrWhiteSpace(message) &&
            message.Contains("problem 2001 (NO_OBJECT)", StringComparison.OrdinalIgnoreCase))
        {
            var operationTargetDescription = BuildMissingOuOperationTargetDescription(operation, details);
            var bestMatchMarker = "best match of:";
            var bestMatchIndex = message.IndexOf(bestMatchMarker, StringComparison.OrdinalIgnoreCase);
            if (bestMatchIndex >= 0)
            {
                var bestMatch = message[(bestMatchIndex + bestMatchMarker.Length)..]
                    .Trim()
                    .Trim('\'', '"');
                description = $"{operationTargetDescription} Best match in AD was '{bestMatch}'.";
                return true;
            }

            description = operationTargetDescription;
            return true;
        }

        description = string.Empty;
        return false;
    }

    private static string GetMissingOuGuidance(string operation, ActiveDirectoryConfig config, string? details)
    {
        if (string.Equals(operation, "command 'CreateUser'", StringComparison.OrdinalIgnoreCase))
        {
            var targetDn = TryGetDetailValue(details, "DistinguishedName");
            var targetOu = TryGetDetailValue(details, "TargetOu");
            return $"Check the exact create target DN and OU, confirm the LDAP server points at the intended writable directory, and verify the bind account can add objects there. Current create target DN='{FormatGuidanceValue(targetDn)}', targetOu='{FormatGuidanceValue(targetOu)}'.";
        }

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

    private static string BuildMissingOuOperationTargetDescription(string operation, string? details)
    {
        if (string.Equals(operation, "command 'CreateUser'", StringComparison.OrdinalIgnoreCase))
        {
            var distinguishedName = TryGetDetailValue(details, "DistinguishedName");
            var targetOu = TryGetDetailValue(details, "TargetOu");
            return $"Active Directory reported NO_OBJECT while creating '{FormatGuidanceValue(distinguishedName)}' under target OU '{FormatGuidanceValue(targetOu)}'.";
        }

        return "The directory search base does not exist or is misconfigured.";
    }

    private static string AppendRawActiveDirectoryMessage(string summary, string? rawMessage)
    {
        if (string.IsNullOrWhiteSpace(rawMessage))
        {
            return summary;
        }

        var trimmedMessage = rawMessage.Trim();
        if (summary.Contains(trimmedMessage, StringComparison.OrdinalIgnoreCase))
        {
            return summary;
        }

        return $"{summary} Raw AD error: {trimmedMessage}";
    }

    private static string? TryGetDetailValue(string? details, string key)
    {
        if (string.IsNullOrWhiteSpace(details) || string.IsNullOrWhiteSpace(key))
        {
            return null;
        }

        var marker = key + "=";
        var startIndex = details.IndexOf(marker, StringComparison.Ordinal);
        if (startIndex < 0)
        {
            return null;
        }

        startIndex += marker.Length;
        var endIndex = details.IndexOf(' ', startIndex);
        var value = endIndex >= 0
            ? details[startIndex..endIndex]
            : details[startIndex..];

        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }

    private static string FormatGuidanceValue(string? value)
    {
        return string.IsNullOrWhiteSpace(value) ? "(unset)" : value;
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

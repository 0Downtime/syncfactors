namespace SyncFactors.Infrastructure;

public static class ActiveDirectoryTransportModeFormatter
{
    public static string DescribeStartupTransport(string? mode)
    {
        if (string.IsNullOrWhiteSpace(mode))
        {
            return "ldaps";
        }

        var trimmedMode = mode.Trim();

        if (string.Equals(trimmedMode, "ldaps", StringComparison.OrdinalIgnoreCase))
        {
            return "ldaps";
        }

        if (string.Equals(trimmedMode, "ldap", StringComparison.OrdinalIgnoreCase))
        {
            return "ldap";
        }

        if (string.Equals(trimmedMode, "starttls", StringComparison.OrdinalIgnoreCase))
        {
            return "starttls (ldap with StartTLS)";
        }

        return trimmedMode;
    }
}

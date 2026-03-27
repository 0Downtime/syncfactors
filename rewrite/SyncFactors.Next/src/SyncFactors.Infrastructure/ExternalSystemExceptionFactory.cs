using System.DirectoryServices.Protocols;

namespace SyncFactors.Infrastructure;

internal static class ExternalSystemExceptionFactory
{
    public static InvalidOperationException CreateActiveDirectoryException(string operation, string server, Exception exception)
    {
        var summary = exception switch
        {
            LdapException ldapException => DescribeLdapFailure(ldapException),
            DirectoryOperationException directoryOperationException => directoryOperationException.Message,
            _ => exception.Message
        };

        return new InvalidOperationException(
            $"Active Directory {operation} failed against LDAP server '{server}'. {summary}",
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
        return new InvalidOperationException(
            $"SuccessFactors {operation} failed for endpoint '{endpoint}'. {summary}",
            innerException);
    }

    private static string DescribeLdapFailure(LdapException exception)
    {
        if (string.Equals(exception.Message, "The supplied credential is invalid.", StringComparison.OrdinalIgnoreCase))
        {
            return "The configured bind credentials were rejected by the directory server.";
        }

        if (string.Equals(exception.Message, "The LDAP server is unavailable.", StringComparison.OrdinalIgnoreCase))
        {
            return "The LDAP server could not be reached.";
        }

        return exception.Message;
    }
}

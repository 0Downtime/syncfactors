using SyncFactors.Contracts;
using SyncFactors.Domain;
using Microsoft.Extensions.Logging;
using System.DirectoryServices.Protocols;
using System.Net;
using System.Diagnostics;

namespace SyncFactors.Infrastructure;

public sealed class ActiveDirectoryGateway(
    SyncFactorsConfigurationLoader configLoader,
    ILogger<ActiveDirectoryGateway> logger) : IDirectoryGateway
{
    private static readonly TimeSpan LdapOperationTimeout = TimeSpan.FromSeconds(10);

    public async Task<DirectoryUserSnapshot?> FindByWorkerAsync(WorkerSnapshot worker, CancellationToken cancellationToken)
    {
        var config = configLoader.GetSyncConfig().Ad;
        if (string.IsNullOrWhiteSpace(config.Server))
        {
            throw new InvalidOperationException("AD server was not configured.");
        }

        try
        {
            var directoryUser = await ExecuteWithTimeoutAsync(
                operation: () => QueryDirectory(worker, config, logger),
                operationName: "lookup",
                server: config.Server,
                cancellationToken: cancellationToken);
            if (directoryUser is not null)
            {
                logger.LogInformation("Resolved directory user from AD. WorkerId={WorkerId} SamAccountName={SamAccountName}", worker.WorkerId, directoryUser.SamAccountName);
                return directoryUser;
            }

            logger.LogInformation("No AD user matched worker. WorkerId={WorkerId}", worker.WorkerId);
            return null;
        }
        catch (LdapException ex)
        {
            logger.LogError(ex, "AD lookup failed with LDAP exception. WorkerId={WorkerId} Server={Server}", worker.WorkerId, config.Server);
            throw ExternalSystemExceptionFactory.CreateActiveDirectoryException("lookup", config.Server, ex);
        }
        catch (DirectoryOperationException ex)
        {
            logger.LogError(ex, "AD lookup failed with directory operation exception. WorkerId={WorkerId} Server={Server}", worker.WorkerId, config.Server);
            throw ExternalSystemExceptionFactory.CreateActiveDirectoryException("lookup", config.Server, ex);
        }
    }

    public async Task<string> ResolveAvailableEmailLocalPartAsync(WorkerSnapshot worker, CancellationToken cancellationToken)
    {
        var baseLocalPart = DirectoryIdentityFormatter.BuildBaseEmailLocalPart(worker.PreferredName, worker.LastName);
        var config = configLoader.GetSyncConfig().Ad;
        if (string.IsNullOrWhiteSpace(config.Server))
        {
            throw new InvalidOperationException("AD server was not configured.");
        }

        try
        {
            var existingDirectoryUser = await ExecuteWithTimeoutAsync(
                operation: () => QueryDirectory(worker, config, logger),
                operationName: "existing worker lookup for email local-part resolution",
                server: config.Server,
                cancellationToken: cancellationToken);

            return await ExecuteWithTimeoutAsync(
                operation: () => ResolveAvailableEmailLocalPart(worker, config, baseLocalPart, existingDirectoryUser?.SamAccountName, logger),
                operationName: "email local-part lookup",
                server: config.Server,
                cancellationToken: cancellationToken);
        }
        catch (LdapException ex)
        {
            logger.LogError(ex, "AD email local part lookup failed with LDAP exception. WorkerId={WorkerId} Server={Server}", worker.WorkerId, config.Server);
            throw ExternalSystemExceptionFactory.CreateActiveDirectoryException("email local-part lookup", config.Server, ex);
        }
        catch (DirectoryOperationException ex)
        {
            logger.LogError(ex, "AD email local part lookup failed with directory operation exception. WorkerId={WorkerId} Server={Server}", worker.WorkerId, config.Server);
            throw ExternalSystemExceptionFactory.CreateActiveDirectoryException("email local-part lookup", config.Server, ex);
        }
    }

    public async Task<string?> ResolveManagerDistinguishedNameAsync(string managerId, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(managerId))
        {
            return null;
        }

        var config = configLoader.GetSyncConfig().Ad;
        if (string.IsNullOrWhiteSpace(config.Server))
        {
            throw new InvalidOperationException("AD server was not configured.");
        }

        try
        {
            return await ExecuteWithTimeoutAsync(
                operation: () => ResolveDistinguishedName(managerId, config, logger),
                operationName: "manager lookup",
                server: config.Server,
                cancellationToken: cancellationToken);
        }
        catch (LdapException ex)
        {
            logger.LogError(ex, "AD manager DN lookup failed with LDAP exception. ManagerId={ManagerId} Server={Server}", managerId, config.Server);
            throw ExternalSystemExceptionFactory.CreateActiveDirectoryException("manager lookup", config.Server, ex);
        }
        catch (DirectoryOperationException ex)
        {
            logger.LogError(ex, "AD manager DN lookup failed with directory operation exception. ManagerId={ManagerId} Server={Server}", managerId, config.Server);
            throw ExternalSystemExceptionFactory.CreateActiveDirectoryException("manager lookup", config.Server, ex);
        }
    }

    private static DirectoryUserSnapshot? QueryDirectory(WorkerSnapshot worker, ActiveDirectoryConfig config, ILogger logger)
    {
        using var connection = CreateConnection(config, logger, $"lookup worker {worker.WorkerId}");
        var request = new SearchRequest(
            config.DefaultActiveOu,
            $"({EscapeLdapFilter(config.IdentityAttribute)}={EscapeLdapFilter(worker.WorkerId)})",
            SearchScope.Subtree,
            "sAMAccountName",
            "distinguishedName",
            "displayName",
            "userAccountControl",
            "givenName",
            "sn",
            "userPrincipalName",
            "mail",
            "department",
            "company",
            "physicalDeliveryOfficeName",
            "streetAddress",
            "l",
            "postalCode",
            "title",
            "division",
            "employeeType",
            "extensionAttribute1",
            "extensionAttribute2",
            "extensionAttribute3",
            "extensionAttribute4");

        var response = ExecuteSearch(
            connection,
            request,
            logger,
            "worker lookup search",
            ("WorkerId", worker.WorkerId),
            ("IdentityAttribute", config.IdentityAttribute),
            ("SearchBase", config.DefaultActiveOu));
        var entry = response.Entries.Cast<SearchResultEntry>().FirstOrDefault();
        if (entry is null)
        {
            return null;
        }

        var samAccountName = GetAttribute(entry, "sAMAccountName");
        var distinguishedName = GetAttribute(entry, "distinguishedName");
        var displayName = GetAttribute(entry, "displayName");
        var userAccountControl = GetAttribute(entry, "userAccountControl");

        return new DirectoryUserSnapshot(
            SamAccountName: samAccountName,
            DistinguishedName: distinguishedName,
            Enabled: ParseEnabled(userAccountControl),
            DisplayName: displayName,
            Attributes: BuildAttributes(entry, displayName));
    }

    private static string? ResolveDistinguishedName(string workerId, ActiveDirectoryConfig config, ILogger logger)
    {
        using var connection = CreateConnection(config, logger, $"resolve manager {workerId}");
        var request = new SearchRequest(
            config.DefaultActiveOu,
            $"({EscapeLdapFilter(config.IdentityAttribute)}={EscapeLdapFilter(workerId)})",
            SearchScope.Subtree,
            "distinguishedName");

        var response = ExecuteSearch(
            connection,
            request,
            logger,
            "manager DN search",
            ("ManagerId", workerId),
            ("IdentityAttribute", config.IdentityAttribute),
            ("SearchBase", config.DefaultActiveOu));
        var entry = response.Entries.Cast<SearchResultEntry>().FirstOrDefault();
        return entry is null ? null : GetAttribute(entry, "distinguishedName");
    }

    private static string ResolveAvailableEmailLocalPart(
        WorkerSnapshot worker,
        ActiveDirectoryConfig config,
        string baseLocalPart,
        string? existingSamAccountName,
        ILogger logger)
    {
        using var connection = CreateConnection(config, logger, $"resolve email local-part for worker {worker.WorkerId}");

        for (var suffix = 0; suffix < 1000; suffix++)
        {
            var candidate = suffix == 0 ? baseLocalPart : $"{baseLocalPart}{suffix + 1}";
            if (!EmailLocalPartExists(connection, config, candidate, existingSamAccountName, logger, worker.WorkerId))
            {
                return candidate;
            }
        }

        throw new InvalidOperationException($"Could not find an available email local part for worker {worker.WorkerId}.");
    }

    private static bool EmailLocalPartExists(
        LdapConnection connection,
        ActiveDirectoryConfig config,
        string localPart,
        string? existingSamAccountName,
        ILogger logger,
        string workerId)
    {
        var userPrincipalName = DirectoryIdentityFormatter.BuildEmailAddress(localPart);
        var request = new SearchRequest(
            config.DefaultActiveOu,
            $"(userPrincipalName={EscapeLdapFilter(userPrincipalName)})",
            SearchScope.Subtree,
            "sAMAccountName");

        var response = ExecuteSearch(
            connection,
            request,
            logger,
            "email local-part search",
            ("WorkerId", workerId),
            ("CandidateUserPrincipalName", userPrincipalName),
            ("SearchBase", config.DefaultActiveOu));

        if (response.Entries.Count == 0)
        {
            return false;
        }

        var matchedSamAccountName = GetAttribute(response.Entries[0], "sAMAccountName");
        return !string.Equals(matchedSamAccountName, existingSamAccountName, StringComparison.OrdinalIgnoreCase);
    }

    private static LdapConnection CreateConnection(ActiveDirectoryConfig config, ILogger logger, string purpose)
    {
        var identifier = new LdapDirectoryIdentifier(config.Server);
        var connection = new LdapConnection(identifier)
        {
            AuthType = string.IsNullOrWhiteSpace(config.Username) ? AuthType.Anonymous : AuthType.Basic,
            Timeout = LdapOperationTimeout
        };

        if (!string.IsNullOrWhiteSpace(config.Username))
        {
            connection.Credential = new NetworkCredential(config.Username, config.BindPassword);
        }

        connection.SessionOptions.ProtocolVersion = 3;
        var stopwatch = Stopwatch.StartNew();
        logger.LogInformation(
            "Starting AD bind. Purpose={Purpose} Server={Server} Username={Username}",
            purpose,
            config.Server,
            string.IsNullOrWhiteSpace(config.Username) ? "anonymous" : config.Username);
        connection.Bind();
        logger.LogInformation(
            "Completed AD bind. Purpose={Purpose} Server={Server} DurationMs={DurationMs}",
            purpose,
            config.Server,
            stopwatch.ElapsedMilliseconds);
        return connection;
    }

    private static string? GetAttribute(SearchResultEntry entry, string attributeName)
    {
        if (!entry.Attributes.Contains(attributeName) || entry.Attributes[attributeName].Count == 0)
        {
            return null;
        }

        return entry.Attributes[attributeName][0]?.ToString();
    }

    private static bool? ParseEnabled(string? userAccountControl)
    {
        if (!int.TryParse(userAccountControl, out var value))
        {
            return null;
        }

        const int AccountDisabledFlag = 0x0002;
        return (value & AccountDisabledFlag) == 0;
    }

    private static IReadOnlyDictionary<string, string?> BuildAttributes(SearchResultEntry entry, string? displayName)
    {
        return new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
        {
            ["displayName"] = displayName,
            ["GivenName"] = GetAttribute(entry, "givenName"),
            ["Surname"] = GetAttribute(entry, "sn"),
            ["UserPrincipalName"] = GetAttribute(entry, "userPrincipalName"),
            ["mail"] = GetAttribute(entry, "mail"),
            ["department"] = GetAttribute(entry, "department"),
            ["company"] = GetAttribute(entry, "company"),
            ["physicalDeliveryOfficeName"] = GetAttribute(entry, "physicalDeliveryOfficeName"),
            ["streetAddress"] = GetAttribute(entry, "streetAddress"),
            ["l"] = GetAttribute(entry, "l"),
            ["postalCode"] = GetAttribute(entry, "postalCode"),
            ["title"] = GetAttribute(entry, "title"),
            ["division"] = GetAttribute(entry, "division"),
            ["employeeType"] = GetAttribute(entry, "employeeType"),
            ["extensionAttribute1"] = GetAttribute(entry, "extensionAttribute1"),
            ["extensionAttribute2"] = GetAttribute(entry, "extensionAttribute2"),
            ["extensionAttribute3"] = GetAttribute(entry, "extensionAttribute3"),
            ["extensionAttribute4"] = GetAttribute(entry, "extensionAttribute4")
        };
    }

    private static async Task<T> ExecuteWithTimeoutAsync<T>(
        Func<T> operation,
        string operationName,
        string server,
        CancellationToken cancellationToken)
    {
        try
        {
            return await Task.Run(operation, cancellationToken).WaitAsync(LdapOperationTimeout, cancellationToken);
        }
        catch (TimeoutException ex)
        {
            throw ExternalSystemExceptionFactory.CreateActiveDirectoryTimeoutException(operationName, server, LdapOperationTimeout, ex);
        }
    }

    private static SearchResponse ExecuteSearch(
        LdapConnection connection,
        SearchRequest request,
        ILogger logger,
        string operation,
        params (string Key, object? Value)[] context)
    {
        var stopwatch = Stopwatch.StartNew();
        logger.LogInformation(
            "Starting AD search. Operation={Operation} Context={Context}",
            operation,
            FormatContext(context));
        var response = (SearchResponse)connection.SendRequest(request);
        logger.LogInformation(
            "Completed AD search. Operation={Operation} DurationMs={DurationMs} Entries={Entries} Context={Context}",
            operation,
            stopwatch.ElapsedMilliseconds,
            response.Entries.Count,
            FormatContext(context));
        return response;
    }

    private static string FormatContext((string Key, object? Value)[] context)
    {
        return string.Join(", ", context.Select(item => $"{item.Key}={item.Value ?? "(null)"}"));
    }

    private static string EscapeLdapFilter(string value)
    {
        return value
            .Replace("\\", "\\5c", StringComparison.Ordinal)
            .Replace("*", "\\2a", StringComparison.Ordinal)
            .Replace("(", "\\28", StringComparison.Ordinal)
            .Replace(")", "\\29", StringComparison.Ordinal)
            .Replace("\0", "\\00", StringComparison.Ordinal);
    }
}

using SyncFactors.Contracts;
using SyncFactors.Domain;
using Microsoft.Extensions.Logging;
using Microsoft.Security.Application;
using System.DirectoryServices.Protocols;
using System.Diagnostics;
using System.Text.RegularExpressions;

namespace SyncFactors.Infrastructure;

public sealed class ActiveDirectoryGateway(
    SyncFactorsConfigurationLoader configLoader,
    ILogger<ActiveDirectoryGateway> logger) : IDirectoryGateway
{
    private static readonly TimeSpan LdapOperationTimeout = TimeSpan.FromSeconds(10);
    private static readonly Regex LdapAttributeNamePattern = new("^[A-Za-z][A-Za-z0-9-]*$", RegexOptions.Compiled);

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
                logger.LogInformation("Resolved directory user from AD.");
                return directoryUser;
            }

            logger.LogInformation("No AD user matched the requested worker.");
            return null;
        }
        catch (LdapException ex)
        {
            logger.LogError(ex, "AD lookup failed with LDAP exception.");
            throw ExternalSystemExceptionFactory.CreateActiveDirectoryException("lookup", config, ex);
        }
        catch (DirectoryOperationException ex)
        {
            logger.LogError(ex, "AD lookup failed with directory operation exception.");
            throw ExternalSystemExceptionFactory.CreateActiveDirectoryException("lookup", config, ex);
        }
    }

    public async Task<string> ResolveAvailableEmailLocalPartAsync(WorkerSnapshot worker, bool isCreate, CancellationToken cancellationToken)
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

            if (!isCreate)
            {
                return baseLocalPart;
            }

            return await ExecuteWithTimeoutAsync(
                operation: () => ResolveAvailableEmailLocalPart(
                    worker,
                    config,
                    baseLocalPart,
                    existingDirectoryUser?.SamAccountName,
                    config.IdentityPolicy.ResolveCreateConflictingUpnAndMail,
                    logger),
                operationName: "email local-part lookup",
                server: config.Server,
                cancellationToken: cancellationToken);
        }
        catch (LdapException ex)
        {
            logger.LogError(ex, "AD email local-part lookup failed with LDAP exception.");
            throw ExternalSystemExceptionFactory.CreateActiveDirectoryException("email local-part lookup", config, ex);
        }
        catch (DirectoryOperationException ex)
        {
            logger.LogError(ex, "AD email local-part lookup failed with directory operation exception.");
            throw ExternalSystemExceptionFactory.CreateActiveDirectoryException("email local-part lookup", config, ex);
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
            logger.LogError(ex, "AD manager DN lookup failed with LDAP exception.");
            throw ExternalSystemExceptionFactory.CreateActiveDirectoryException("manager lookup", config, ex);
        }
        catch (DirectoryOperationException ex)
        {
            logger.LogError(ex, "AD manager DN lookup failed with directory operation exception.");
            throw ExternalSystemExceptionFactory.CreateActiveDirectoryException("manager lookup", config, ex);
        }
    }

    public async Task<IReadOnlyList<DirectoryUserSnapshot>> ListUsersInOuAsync(string ouDistinguishedName, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(ouDistinguishedName))
        {
            return [];
        }

        var config = configLoader.GetSyncConfig().Ad;
        if (string.IsNullOrWhiteSpace(config.Server))
        {
            throw new InvalidOperationException("AD server was not configured.");
        }

        try
        {
            return await ExecuteWithTimeoutAsync(
                operation: () => QueryUsersInOu(ouDistinguishedName, config, logger),
                operationName: "ou listing",
                server: config.Server,
                cancellationToken: cancellationToken);
        }
        catch (LdapException ex)
        {
            logger.LogError(ex, "AD OU listing failed with LDAP exception.");
            throw ExternalSystemExceptionFactory.CreateActiveDirectoryException("ou listing", config, ex);
        }
        catch (DirectoryOperationException ex)
        {
            logger.LogError(ex, "AD OU listing failed with directory operation exception.");
            throw ExternalSystemExceptionFactory.CreateActiveDirectoryException("ou listing", config, ex);
        }
    }

    private static DirectoryUserSnapshot? QueryDirectory(WorkerSnapshot worker, ActiveDirectoryConfig config, ILogger logger)
    {
        using var connection = CreateConnection(config, logger);
        var entry = FindFirstEntry(
            connection,
            GetSearchBases(config),
            searchAttribute: config.IdentityAttribute,
            searchValue: worker.WorkerId,
            config.IdentityAttribute,
            logger,
            "worker lookup search");
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
            Attributes: BuildAttributes(entry, displayName, config.IdentityAttribute));
    }

    private static IReadOnlyList<DirectoryUserSnapshot> QueryUsersInOu(string ouDistinguishedName, ActiveDirectoryConfig config, ILogger logger)
    {
        using var connection = CreateConnection(config, logger);
        var request = new SearchRequest(
            ouDistinguishedName,
            "(&(objectCategory=person)(objectClass=user))",
            SearchScope.Subtree,
            "sAMAccountName",
            "cn",
            "distinguishedName",
            "displayName",
            "userAccountControl",
            config.IdentityAttribute,
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
            "manager",
            "extensionAttribute1",
            "extensionAttribute2",
            "extensionAttribute3",
            "extensionAttribute4",
            "extensionAttribute5",
            "extensionAttribute6",
            "extensionAttribute7",
            "extensionAttribute8",
            "extensionAttribute9",
            "extensionAttribute10",
            "extensionAttribute11",
            "extensionAttribute12",
            "extensionAttribute13",
            "extensionAttribute14",
            "extensionAttribute15");

        var response = ExecuteSearch(connection, request, logger, "ou listing");
        return response.Entries.Cast<SearchResultEntry>()
            .Select(entry =>
            {
                var distinguishedName = GetAttribute(entry, "distinguishedName");
                var displayName = GetAttribute(entry, "displayName");
                var userAccountControl = GetAttribute(entry, "userAccountControl");

                return new DirectoryUserSnapshot(
                    SamAccountName: GetAttribute(entry, "sAMAccountName"),
                    DistinguishedName: distinguishedName,
                    Enabled: ParseEnabled(userAccountControl),
                    DisplayName: displayName,
                    Attributes: BuildAttributes(entry, displayName, config.IdentityAttribute));
            })
            .ToArray();
    }

    private static string? ResolveDistinguishedName(string workerId, ActiveDirectoryConfig config, ILogger logger)
    {
        using var connection = CreateConnection(config, logger);
        var entry = FindFirstEntry(
            connection,
            GetSearchBases(config),
            searchAttribute: config.IdentityAttribute,
            searchValue: workerId,
            "distinguishedName",
            logger,
            "manager DN search");
        return entry is null ? null : GetAttribute(entry, "distinguishedName");
    }

    private static string ResolveAvailableEmailLocalPart(
        WorkerSnapshot worker,
        ActiveDirectoryConfig config,
        string baseLocalPart,
        string? existingSamAccountName,
        bool resolveCreateConflictingUpnAndMail,
        ILogger logger)
    {
        if (!resolveCreateConflictingUpnAndMail)
        {
            return baseLocalPart;
        }

        using var connection = CreateConnection(config, logger);
        var searchBases = GetEmailUniquenessSearchBases(connection, config, logger);
        return ResolveAvailableEmailLocalPart(
            worker.WorkerId,
            baseLocalPart,
            candidate => EmailLocalPartExists(connection, searchBases, candidate, existingSamAccountName, logger, worker.WorkerId));
    }

    private static string ResolveAvailableEmailLocalPart(
        string workerId,
        string baseLocalPart,
        Func<string, bool> candidateExists)
    {
        for (var suffix = 0; suffix < 1000; suffix++)
        {
            var candidate = suffix == 0 ? baseLocalPart : $"{baseLocalPart}{suffix + 1}";
            if (!candidateExists(candidate))
            {
                return candidate;
            }
        }

        throw new InvalidOperationException($"Could not find an available email local part for worker {workerId}.");
    }

    private static bool EmailLocalPartExists(
        LdapConnection connection,
        IReadOnlyList<string> searchBases,
        string localPart,
        string? existingSamAccountName,
        ILogger logger,
        string workerId)
    {
        var userPrincipalName = DirectoryIdentityFormatter.BuildEmailAddress(localPart);
        var entry = FindFirstEntryMatchingAny(
            connection,
            searchBases,
            [("userPrincipalName", userPrincipalName), ("mail", userPrincipalName)],
            "mail",
            logger,
            "email local-part search");

        if (entry is null)
        {
            return false;
        }

        var matchedSamAccountName = GetAttribute(entry, "sAMAccountName");
        return !string.Equals(matchedSamAccountName, existingSamAccountName, StringComparison.OrdinalIgnoreCase);
    }

    private static IReadOnlyList<string> GetEmailUniquenessSearchBases(
        LdapConnection connection,
        ActiveDirectoryConfig config,
        ILogger logger)
    {
        var defaultNamingContext = TryGetDefaultNamingContext(connection, logger);
        return GetEmailUniquenessSearchBases(defaultNamingContext, config);
    }

    private static IReadOnlyList<string> GetEmailUniquenessSearchBases(string? defaultNamingContext, ActiveDirectoryConfig config)
    {
        if (!string.IsNullOrWhiteSpace(defaultNamingContext))
        {
            return [defaultNamingContext.Trim()];
        }

        return GetSearchBases(config);
    }

    private static string? TryGetDefaultNamingContext(LdapConnection connection, ILogger logger)
    {
        var request = new SearchRequest(
            string.Empty,
            "(objectClass=*)",
            SearchScope.Base,
            "defaultNamingContext");

        var response = ExecuteSearch(connection, request, logger, "rootdse naming context search");
        var entry = response.Entries.Cast<SearchResultEntry>().FirstOrDefault();
        return entry is null ? null : GetAttribute(entry, "defaultNamingContext");
    }

    private static SearchResultEntry? FindFirstEntry(
        LdapConnection connection,
        IReadOnlyList<string> searchBases,
        string searchAttribute,
        string searchValue,
        string additionalAttribute,
        ILogger logger,
        string operation)
    {
        foreach (var searchBase in searchBases)
        {
            var request = CreateSearchRequest(
                searchBase,
                searchAttribute,
                searchValue,
                additionalAttribute);

            var response = ExecuteSearch(
                connection,
                request,
                logger,
                operation);
            var entry = response.Entries.Cast<SearchResultEntry>().FirstOrDefault();
            if (entry is not null)
            {
                return entry;
            }
        }

        return null;
    }

    private static SearchResultEntry? FindFirstEntryMatchingAny(
        LdapConnection connection,
        IReadOnlyList<string> searchBases,
        IReadOnlyList<(string Attribute, string Value)> searchClauses,
        string additionalAttribute,
        ILogger logger,
        string operation)
    {
        foreach (var searchBase in searchBases)
        {
            var request = CreateSearchRequest(
                searchBase,
                searchClauses,
                additionalAttribute);

            var response = ExecuteSearch(
                connection,
                request,
                logger,
                operation);
            var entry = response.Entries.Cast<SearchResultEntry>().FirstOrDefault();
            if (entry is not null)
            {
                return entry;
            }
        }

        return null;
    }

    private static SearchRequest CreateSearchRequest(
        string searchBase,
        string searchAttribute,
        string searchValue,
        string additionalAttribute)
    {
        return new SearchRequest(
            searchBase,
            BuildEqualityFilter(searchAttribute, searchValue),
            SearchScope.Subtree,
            "sAMAccountName",
            "cn",
            "distinguishedName",
            "displayName",
            "userAccountControl",
            additionalAttribute,
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
            "manager",
            "extensionAttribute1",
            "extensionAttribute2",
            "extensionAttribute3",
            "extensionAttribute4",
            "extensionAttribute5",
            "extensionAttribute6",
            "extensionAttribute7",
            "extensionAttribute8",
            "extensionAttribute9",
            "extensionAttribute10",
            "extensionAttribute11",
            "extensionAttribute12",
            "extensionAttribute13",
            "extensionAttribute14",
            "extensionAttribute15");
    }

    private static SearchRequest CreateSearchRequest(
        string searchBase,
        IReadOnlyList<(string Attribute, string Value)> searchClauses,
        string additionalAttribute)
    {
        return new SearchRequest(
            searchBase,
            BuildAnyOfEqualityFilter(searchClauses),
            SearchScope.Subtree,
            "sAMAccountName",
            "cn",
            "distinguishedName",
            "displayName",
            "userAccountControl",
            additionalAttribute,
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
            "manager",
            "extensionAttribute1",
            "extensionAttribute2",
            "extensionAttribute3",
            "extensionAttribute4",
            "extensionAttribute5",
            "extensionAttribute6",
            "extensionAttribute7",
            "extensionAttribute8",
            "extensionAttribute9",
            "extensionAttribute10",
            "extensionAttribute11",
            "extensionAttribute12",
            "extensionAttribute13",
            "extensionAttribute14",
            "extensionAttribute15");
    }

    private static IReadOnlyList<string> GetSearchBases(ActiveDirectoryConfig config)
    {
        return new[] { config.DefaultActiveOu, config.PrehireOu, config.GraveyardOu, config.LeaveOu }
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static LdapConnection CreateConnection(ActiveDirectoryConfig config, ILogger logger)
        => ActiveDirectoryConnectionFactory.CreateConnection(config, logger, LdapOperationTimeout);

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

    private static IReadOnlyDictionary<string, string?> BuildAttributes(
        SearchResultEntry entry,
        string? displayName,
        string identityAttribute)
    {
        var attributes = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
        {
            ["sAMAccountName"] = GetAttribute(entry, "sAMAccountName"),
            ["cn"] = GetAttribute(entry, "cn"),
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
            ["manager"] = GetAttribute(entry, "manager"),
            ["extensionAttribute1"] = GetAttribute(entry, "extensionAttribute1"),
            ["extensionAttribute2"] = GetAttribute(entry, "extensionAttribute2"),
            ["extensionAttribute3"] = GetAttribute(entry, "extensionAttribute3"),
            ["extensionAttribute4"] = GetAttribute(entry, "extensionAttribute4"),
            ["extensionAttribute5"] = GetAttribute(entry, "extensionAttribute5"),
            ["extensionAttribute6"] = GetAttribute(entry, "extensionAttribute6"),
            ["extensionAttribute7"] = GetAttribute(entry, "extensionAttribute7"),
            ["extensionAttribute8"] = GetAttribute(entry, "extensionAttribute8"),
            ["extensionAttribute9"] = GetAttribute(entry, "extensionAttribute9"),
            ["extensionAttribute10"] = GetAttribute(entry, "extensionAttribute10"),
            ["extensionAttribute11"] = GetAttribute(entry, "extensionAttribute11"),
            ["extensionAttribute12"] = GetAttribute(entry, "extensionAttribute12"),
            ["extensionAttribute13"] = GetAttribute(entry, "extensionAttribute13"),
            ["extensionAttribute14"] = GetAttribute(entry, "extensionAttribute14"),
            ["extensionAttribute15"] = GetAttribute(entry, "extensionAttribute15")
        };

        if (!string.IsNullOrWhiteSpace(identityAttribute))
        {
            attributes[identityAttribute] = GetAttribute(entry, identityAttribute);
        }

        return attributes;
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
        string operation)
    {
        var stopwatch = Stopwatch.StartNew();
        logger.LogInformation("Starting AD search. Operation={Operation}", operation);
        var response = (SearchResponse)connection.SendRequest(request);
        logger.LogInformation(
            "Completed AD search. Operation={Operation} DurationMs={DurationMs} Entries={Entries}",
            operation,
            stopwatch.ElapsedMilliseconds,
            response.Entries.Count);
        return response;
    }

    private static string BuildEqualityFilter(string attributeName, string value)
    {
        return $"({ValidateLdapAttributeName(attributeName)}={Encoder.LdapFilterEncode(value)})";
    }

    private static string BuildAnyOfEqualityFilter(IReadOnlyList<(string Attribute, string Value)> clauses)
    {
        if (clauses.Count == 0)
        {
            throw new InvalidOperationException("At least one LDAP search clause is required.");
        }

        return clauses.Count == 1
            ? BuildEqualityFilter(clauses[0].Attribute, clauses[0].Value)
            : $"(|{string.Concat(clauses.Select(clause => BuildEqualityFilter(clause.Attribute, clause.Value)))})";
    }

    private static string ValidateLdapAttributeName(string value)
    {
        if (!LdapAttributeNamePattern.IsMatch(value))
        {
            throw new InvalidOperationException($"Invalid LDAP attribute name '{value}'.");
        }

        return value;
    }
}

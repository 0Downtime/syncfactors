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
    IAttributeMappingProvider attributeMappingProvider,
    IActiveDirectoryConnectionPool connectionPool,
    ILogger<ActiveDirectoryGateway> logger) : IDirectoryGateway
{
    private static readonly TimeSpan LdapOperationTimeout = TimeSpan.FromSeconds(10);
    private static readonly Regex LdapAttributeNamePattern = new("^[A-Za-z][A-Za-z0-9-]*$", RegexOptions.Compiled);
    private const int OuListingPageSize = 500;
    private const int MaxTransientLdapRetries = 1;

    public async Task<DirectoryUserSnapshot?> FindByWorkerAsync(WorkerSnapshot worker, CancellationToken cancellationToken)
    {
        var config = configLoader.GetSyncConfig().Ad;
        if (string.IsNullOrWhiteSpace(config.Server))
        {
            throw new InvalidOperationException("AD server was not configured.");
        }

        var directoryUser = await ExecuteWithTransientLdapRetryAsync(
            config,
            operationName: "lookup",
            cancellationToken,
            lease => QueryDirectory(lease.Connection, worker, config, attributeMappingProvider.GetEnabledMappings(), logger));
        if (directoryUser is not null)
        {
            logger.LogInformation("Resolved directory user from AD.");
            return directoryUser;
        }

        logger.LogInformation("No AD user matched the requested worker.");
        return null;
    }

    public async Task<string> ResolveAvailableEmailLocalPartAsync(WorkerSnapshot worker, bool isCreate, CancellationToken cancellationToken)
    {
        return await ResolveAvailableEmailLocalPartAsync(worker, isCreate, existingDirectoryUser: null, cancellationToken);
    }

    public async Task<string> ResolveAvailableEmailLocalPartAsync(
        WorkerSnapshot worker,
        bool isCreate,
        DirectoryUserSnapshot? existingDirectoryUser,
        CancellationToken cancellationToken)
    {
        var baseLocalPart = DirectoryIdentityFormatter.BuildPreferredEmailLocalPart(worker.PreferredName, worker.LastName, worker.WorkerId);
        var config = configLoader.GetSyncConfig().Ad;
        if (string.IsNullOrWhiteSpace(config.Server))
        {
            throw new InvalidOperationException("AD server was not configured.");
        }

        if (!isCreate)
        {
            return baseLocalPart;
        }

        return await ExecuteWithTransientLdapRetryAsync(
            config,
            operationName: "email local-part lookup",
            cancellationToken,
            lease => ResolveAvailableEmailLocalPart(
                lease.Connection,
                worker,
                config,
                baseLocalPart,
                existingDirectoryUser?.SamAccountName,
                config.IdentityPolicy.ResolveCreateConflictingUpnAndMail,
                logger));
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

        return await ExecuteWithTransientLdapRetryAsync(
            config,
            operationName: "manager lookup",
            cancellationToken,
            lease => ResolveDistinguishedName(lease.Connection, managerId, config, logger));
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

        return await ExecuteWithTransientLdapRetryAsync(
            config,
            operationName: "ou listing",
            cancellationToken,
            lease => QueryUsersInOu(lease.Connection, ouDistinguishedName, config, logger));
    }

    private async Task<T> ExecuteWithTransientLdapRetryAsync<T>(
        ActiveDirectoryConfig config,
        string operationName,
        CancellationToken cancellationToken,
        Func<ActiveDirectoryConnectionPool.ActiveDirectoryConnectionLease, T> operation)
    {
        for (var attempt = 0; ; attempt++)
        {
            ActiveDirectoryConnectionPool.ActiveDirectoryConnectionLease? lease = null;
            try
            {
                lease = connectionPool.Lease(config, logger, LdapOperationTimeout);
                logger.LogInformation(
                    "AD {Operation} acquired {ConnectionSource} connection. Server={Server} RequestedTransport={RequestedTransport} EffectiveTransport={EffectiveTransport} UsedFallback={UsedFallback} Attempt={Attempt}",
                    operationName,
                    lease.WasReused ? "pooled" : "fresh",
                    config.Server,
                    lease.RequestedTransport,
                    lease.EffectiveTransport,
                    lease.UsedFallback,
                    attempt + 1);
                return await ExecuteWithTimeoutAsync(
                    operation: () => operation(lease),
                    operationName,
                    server: config.Server,
                    cancellationToken);
            }
            catch (LdapException ex) when (ShouldRetryTransientLdapFailure(ex, attempt))
            {
                lease?.Invalidate();
                connectionPool.InvalidateIdleConnections(config);
                logger.LogWarning(
                    ex,
                    "AD {Operation} hit a transient LDAP availability failure. Server={Server} ConnectionSource={ConnectionSource} RequestedTransport={RequestedTransport} EffectiveTransport={EffectiveTransport} UsedFallback={UsedFallback} Attempt={Attempt}. Flushed pooled idle connections and retrying with a fresh connection.",
                    operationName,
                    config.Server,
                    lease?.WasReused == true ? "pooled" : "fresh",
                    lease?.RequestedTransport,
                    lease?.EffectiveTransport,
                    lease?.UsedFallback,
                    attempt + 1);
            }
            catch (InvalidOperationException ex) when (ShouldRetryTransientLdapFailure(ex, attempt))
            {
                lease?.Invalidate();
                connectionPool.InvalidateIdleConnections(config);
                logger.LogWarning(
                    ex,
                    "AD {Operation} hit a transient LDAP timeout. Server={Server} ConnectionSource={ConnectionSource} RequestedTransport={RequestedTransport} EffectiveTransport={EffectiveTransport} UsedFallback={UsedFallback} Attempt={Attempt}. Flushed pooled idle connections and retrying with a fresh connection.",
                    operationName,
                    config.Server,
                    lease?.WasReused == true ? "pooled" : "fresh",
                    lease?.RequestedTransport,
                    lease?.EffectiveTransport,
                    lease?.UsedFallback,
                    attempt + 1);
            }
            catch (LdapException ex)
            {
                lease?.Invalidate();
                logger.LogError(
                    ex,
                    "AD {Operation} failed with LDAP exception. Server={Server} ConnectionSource={ConnectionSource} RequestedTransport={RequestedTransport} EffectiveTransport={EffectiveTransport} UsedFallback={UsedFallback} Attempt={Attempt}",
                    operationName,
                    config.Server,
                    lease?.WasReused == true ? "pooled" : "fresh",
                    lease?.RequestedTransport,
                    lease?.EffectiveTransport,
                    lease?.UsedFallback,
                    attempt + 1);
                throw ExternalSystemExceptionFactory.CreateActiveDirectoryException(operationName, config, ex);
            }
            catch (DirectoryOperationException ex)
            {
                lease?.Invalidate();
                logger.LogError(
                    ex,
                    "AD {Operation} failed with directory operation exception. Server={Server} ConnectionSource={ConnectionSource} RequestedTransport={RequestedTransport} EffectiveTransport={EffectiveTransport} UsedFallback={UsedFallback} Attempt={Attempt}",
                    operationName,
                    config.Server,
                    lease?.WasReused == true ? "pooled" : "fresh",
                    lease?.RequestedTransport,
                    lease?.EffectiveTransport,
                    lease?.UsedFallback,
                    attempt + 1);
                throw ExternalSystemExceptionFactory.CreateActiveDirectoryException(operationName, config, ex);
            }
            catch
            {
                lease?.Invalidate();
                throw;
            }
            finally
            {
                lease?.Dispose();
            }
        }
    }

    private static DirectoryUserSnapshot? QueryDirectory(
        LdapConnection connection,
        WorkerSnapshot worker,
        ActiveDirectoryConfig config,
        IReadOnlyList<AttributeMapping> mappings,
        ILogger logger)
    {
        var lookupClauses = BuildLookupClauses(worker, config.IdentityAttribute, mappings);
        var searchBases = GetSearchBases(config);
        logger.LogInformation(
            "Starting AD worker lookup. WorkerId={WorkerId} IdentityAttribute={IdentityAttribute} Clauses={Clauses} SearchBases={SearchBases}",
            worker.WorkerId,
            config.IdentityAttribute,
            FormatLookupClauses(lookupClauses),
            FormatSearchBases(searchBases));

        var entry = FindUniqueEntryMatchingAny(
            connection,
            searchBases,
            lookupClauses,
            config.IdentityAttribute,
            lookupKind: "worker identity",
            lookupValue: worker.WorkerId,
            logger,
            "worker lookup search");
        if (entry is null)
        {
            logger.LogInformation(
                "No AD entry matched worker lookup. WorkerId={WorkerId} IdentityAttribute={IdentityAttribute} Clauses={Clauses} SearchBases={SearchBases}",
                worker.WorkerId,
                config.IdentityAttribute,
                FormatLookupClauses(lookupClauses),
                FormatSearchBases(searchBases));
            return null;
        }

        var samAccountName = GetAttribute(entry, "sAMAccountName");
        var distinguishedName = GetAttribute(entry, "distinguishedName");
        var displayName = GetAttribute(entry, "displayName");
        var userAccountControl = GetAttribute(entry, "userAccountControl");
        var attributes = BuildAttributes(entry, displayName, config.IdentityAttribute);
        var identityValue = attributes.TryGetValue(config.IdentityAttribute, out var resolvedIdentityValue)
            ? resolvedIdentityValue
            : null;

        if (!string.IsNullOrWhiteSpace(config.IdentityAttribute) &&
            string.IsNullOrWhiteSpace(identityValue))
        {
            logger.LogWarning(
                "Matched AD entry did not return the configured identity attribute. WorkerId={WorkerId} SamAccountName={SamAccountName} DistinguishedName={DistinguishedName} IdentityAttribute={IdentityAttribute} LookupClauses={Clauses} ReturnedAttributes={ReturnedAttributes}",
                worker.WorkerId,
                samAccountName,
                distinguishedName,
                config.IdentityAttribute,
                FormatLookupClauses(lookupClauses),
                FormatReturnedAttributeNames(entry));
        }

        logger.LogInformation(
            "Resolved AD worker snapshot. WorkerId={WorkerId} SamAccountName={SamAccountName} DistinguishedName={DistinguishedName} IdentityAttribute={IdentityAttribute} IdentityValue={IdentityValue}",
            worker.WorkerId,
            samAccountName,
            distinguishedName,
            config.IdentityAttribute,
            identityValue);

        return new DirectoryUserSnapshot(
            SamAccountName: samAccountName,
            DistinguishedName: distinguishedName,
            Enabled: ParseEnabled(userAccountControl),
            DisplayName: displayName,
            Attributes: attributes);
    }

    private static IReadOnlyList<(string Attribute, string Value)> BuildLookupClauses(
        WorkerSnapshot worker,
        string identityAttribute,
        IReadOnlyList<AttributeMapping> mappings)
    {
        var clauses = new List<(string Attribute, string Value)>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        AddLookupClause(clauses, seen, identityAttribute, ResolveDirectoryIdentityValue(worker, identityAttribute, mappings));
        AddLookupClause(clauses, seen, "sAMAccountName", worker.WorkerId);

        if (clauses.Count == 0)
        {
            throw new InvalidOperationException("Could not determine any AD lookup clauses for the worker.");
        }

        return clauses;
    }

    private static void AddLookupClause(
        ICollection<(string Attribute, string Value)> clauses,
        ISet<string> seen,
        string attribute,
        string? value)
    {
        if (string.IsNullOrWhiteSpace(attribute) || string.IsNullOrWhiteSpace(value))
        {
            return;
        }

        var trimmedValue = value.Trim();
        if (!seen.Add($"{attribute}\0{trimmedValue}"))
        {
            return;
        }

        clauses.Add((attribute, trimmedValue));
    }

    private static string FormatLookupClauses(IReadOnlyList<(string Attribute, string Value)> clauses)
    {
        return string.Join(" | ", clauses.Select(clause => $"{clause.Attribute}={clause.Value}"));
    }

    private static string FormatSearchBases(IReadOnlyList<string> searchBases)
    {
        return string.Join(" | ", searchBases);
    }

    private static string FormatReturnedAttributeNames(SearchResultEntry entry)
    {
        return string.Join(
            ",",
            entry.Attributes.AttributeNames
                .Cast<string>()
                .OrderBy(name => name, StringComparer.OrdinalIgnoreCase));
    }

    private static string ResolveDirectoryIdentityValue(
        WorkerSnapshot worker,
        string identityAttribute,
        IReadOnlyList<AttributeMapping> mappings)
    {
        if (worker.Attributes.TryGetValue(identityAttribute, out var directValue) &&
            !string.IsNullOrWhiteSpace(directValue))
        {
            return directValue.Trim();
        }

        var mapping = mappings.FirstOrDefault(candidate =>
            string.Equals(candidate.Target, identityAttribute, StringComparison.OrdinalIgnoreCase));
        if (mapping is not null)
        {
            var mappedValue = ApplyMappingTransform(ResolveMappedSourceValue(worker, mapping.Source), mapping.Transform);
            if (!string.IsNullOrWhiteSpace(mappedValue))
            {
                return mappedValue;
            }
        }

        if (string.Equals(identityAttribute, "employeeID", StringComparison.OrdinalIgnoreCase) &&
            worker.Attributes.TryGetValue("personIdExternal", out var personIdExternal) &&
            !string.IsNullOrWhiteSpace(personIdExternal))
        {
            return personIdExternal.Trim();
        }

        return worker.WorkerId;
    }

    private static string? ResolveMappedSourceValue(WorkerSnapshot worker, string source)
    {
        if (string.IsNullOrWhiteSpace(source))
        {
            return null;
        }

        const string concatPrefix = "Concat(";
        if (source.StartsWith(concatPrefix, StringComparison.OrdinalIgnoreCase) && source.EndsWith(')'))
        {
            var parts = source[concatPrefix.Length..^1]
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(part => ResolveMappedSourceValue(worker, part))
                .Where(part => !string.IsNullOrWhiteSpace(part))
                .Select(part => part!.Trim())
                .ToArray();

            return parts.Length == 0 ? null : string.Join(' ', parts);
        }

        if (worker.Attributes.TryGetValue(source, out var directValue) &&
            !string.IsNullOrWhiteSpace(directValue))
        {
            return directValue;
        }

        var normalizedSource = NormalizeSourcePath(source);
        if (!string.Equals(normalizedSource, source, StringComparison.OrdinalIgnoreCase) &&
            worker.Attributes.TryGetValue(normalizedSource, out var normalizedValue) &&
            !string.IsNullOrWhiteSpace(normalizedValue))
        {
            return normalizedValue;
        }

        return normalizedSource switch
        {
            "preferredName" => worker.PreferredName,
            "firstName" => worker.PreferredName,
            "lastName" => worker.LastName,
            "department" => worker.Department,
            _ => null
        };
    }

    private static string? ApplyMappingTransform(string? value, string transform)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return value;
        }

        return transform switch
        {
            "Trim" => value.Trim(),
            "Lower" => value.Trim().ToLowerInvariant(),
            "TrimStripCommasPeriods" => new string(value.Trim().Where(character => character is not ',' and not '.').ToArray()),
            _ => value
        };
    }

    private static string NormalizeSourcePath(string source)
    {
        return source switch
        {
            "personalInfoNav[0].firstName" => "firstName",
            "personalInfoNav[0].lastName" => "lastName",
            "personalInfoNav[0].preferredName" => "preferredName",
            "personalInfoNav[0].displayName" => "displayName",
            "emailNav[0].emailAddress" => "email",
            "emailNav[?(@.isPrimary == true)].emailAddress" => "email",
            "employmentNav[0].startDate" => "startDate",
            "employmentNav[0].userNav.manager.empInfo.personIdExternal" => "managerId",
            _ => source
        };
    }

    private static IReadOnlyList<DirectoryUserSnapshot> QueryUsersInOu(LdapConnection connection, string ouDistinguishedName, ActiveDirectoryConfig config, ILogger logger)
    {
        var request = CreateOuListingRequest(ouDistinguishedName, config);
        var users = new List<DirectoryUserSnapshot>();
        byte[]? pageCookie = null;

        do
        {
            ApplyPageCookie(request, pageCookie);
            var response = ExecuteSearch(connection, request, logger, "ou listing");
            users.AddRange(
                response.Entries.Cast<SearchResultEntry>()
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
                    }));

            pageCookie = GetPageCookie(response);
        }
        while (pageCookie is { Length: > 0 });

        return users.ToArray();
    }

    private static SearchRequest CreateOuListingRequest(string ouDistinguishedName, ActiveDirectoryConfig config)
    {
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
        request.Controls.Add(new PageResultRequestControl(OuListingPageSize));

        return request;
    }

    private static void ApplyPageCookie(SearchRequest request, byte[]? pageCookie)
    {
        var pageControl = request.Controls.OfType<PageResultRequestControl>().Single();
        pageControl.Cookie = pageCookie ?? [];
    }

    private static byte[]? GetPageCookie(SearchResponse response)
    {
        return response.Controls
            .OfType<PageResultResponseControl>()
            .SingleOrDefault()?
            .Cookie;
    }

    private static string? ResolveDistinguishedName(LdapConnection connection, string workerId, ActiveDirectoryConfig config, ILogger logger)
    {
        var entry = FindUniqueEntry(
            connection,
            GetSearchBases(config),
            searchAttribute: config.IdentityAttribute,
            searchValue: workerId,
            "distinguishedName",
            lookupKind: "manager identity",
            logger,
            "manager DN search");
        return entry is null ? null : GetAttribute(entry, "distinguishedName");
    }

    private static string ResolveAvailableEmailLocalPart(
        LdapConnection connection,
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

        var searchBases = GetEmailUniquenessSearchBases(connection, config, logger);
        return ResolveAvailableEmailLocalPart(
            worker.WorkerId,
            baseLocalPart,
            candidate => EmailLocalPartExists(connection, searchBases, candidate, existingSamAccountName, config.UpnSuffix, logger, worker.WorkerId));
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
        string upnSuffix,
        ILogger logger,
        string workerId)
    {
        var userPrincipalName = DirectoryIdentityFormatter.BuildEmailAddress(localPart, upnSuffix);
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

    private static SearchResultEntry? FindUniqueEntry(
        LdapConnection connection,
        IReadOnlyList<string> searchBases,
        string searchAttribute,
        string searchValue,
        string additionalAttribute,
        string lookupKind,
        ILogger logger,
        string operation)
    {
        var matches = new List<SearchResultEntry>();
        var seenDistinguishedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var searchBase in searchBases)
        {
            var request = CreateSearchRequest(
                searchBase,
                searchAttribute,
                searchValue,
                additionalAttribute);

            SearchResponse response;
            try
            {
                response = ExecuteSearch(
                    connection,
                    request,
                    logger,
                    operation);
            }
            catch (DirectoryOperationException ex) when (IsReferralException(ex))
            {
                logger.LogWarning(ex, "Skipping AD search base because the server returned a referral. SearchBase={SearchBase} Operation={Operation}", searchBase, operation);
                continue;
            }
            catch (LdapException ex) when (IsReferralException(ex))
            {
                logger.LogWarning(ex, "Skipping AD search base because the LDAP client encountered a referral. SearchBase={SearchBase} Operation={Operation}", searchBase, operation);
                continue;
            }

            foreach (var entry in response.Entries.Cast<SearchResultEntry>())
            {
                var distinguishedName = entry.DistinguishedName;
                if (string.IsNullOrWhiteSpace(distinguishedName) || seenDistinguishedNames.Add(distinguishedName))
                {
                    matches.Add(entry);
                }
            }
        }

        return EnsureSingleMatch(matches, lookupKind, searchValue, searchAttribute);
    }

    private static SearchResultEntry? FindUniqueEntryMatchingAny(
        LdapConnection connection,
        IReadOnlyList<string> searchBases,
        IReadOnlyList<(string Attribute, string Value)> searchClauses,
        string additionalAttribute,
        string lookupKind,
        string lookupValue,
        ILogger logger,
        string operation)
    {
        var matches = new List<SearchResultEntry>();
        var seenDistinguishedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var searchBase in searchBases)
        {
            var request = CreateSearchRequest(
                searchBase,
                searchClauses,
                additionalAttribute);

            SearchResponse response;
            try
            {
                response = ExecuteSearch(
                    connection,
                    request,
                    logger,
                    operation);
            }
            catch (DirectoryOperationException ex) when (IsReferralException(ex))
            {
                logger.LogWarning(ex, "Skipping AD search base because the server returned a referral. SearchBase={SearchBase} Operation={Operation}", searchBase, operation);
                continue;
            }
            catch (LdapException ex) when (IsReferralException(ex))
            {
                logger.LogWarning(ex, "Skipping AD search base because the LDAP client encountered a referral. SearchBase={SearchBase} Operation={Operation}", searchBase, operation);
                continue;
            }

            foreach (var entry in response.Entries.Cast<SearchResultEntry>())
            {
                var distinguishedName = entry.DistinguishedName;
                if (string.IsNullOrWhiteSpace(distinguishedName) || seenDistinguishedNames.Add(distinguishedName))
                {
                    matches.Add(entry);
                }
            }
        }

        return EnsureSingleMatch(matches, lookupKind, lookupValue, ResolveIdentityAttribute(searchClauses));
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

            SearchResponse response;
            try
            {
                response = ExecuteSearch(
                    connection,
                    request,
                    logger,
                    operation);
            }
            catch (DirectoryOperationException ex) when (IsReferralException(ex))
            {
                logger.LogWarning(ex, "Skipping AD search base because the server returned a referral. SearchBase={SearchBase} Operation={Operation}", searchBase, operation);
                continue;
            }
            catch (LdapException ex) when (IsReferralException(ex))
            {
                logger.LogWarning(ex, "Skipping AD search base because the LDAP client encountered a referral. SearchBase={SearchBase} Operation={Operation}", searchBase, operation);
                continue;
            }

            var entry = response.Entries.Cast<SearchResultEntry>().FirstOrDefault();
            if (entry is not null)
            {
                return entry;
            }
        }

        return null;
    }

    private static SearchResultEntry? EnsureSingleMatch(
        IReadOnlyList<SearchResultEntry> matches,
        string lookupKind,
        string lookupValue,
        string identityAttribute)
    {
        if (matches.Count <= 1)
        {
            return matches.FirstOrDefault();
        }

        throw CreateAmbiguousDirectoryIdentityException(
            lookupKind,
            lookupValue,
            identityAttribute,
            matches
                .Select(match => match.DistinguishedName)
                .Where(distinguishedName => !string.IsNullOrWhiteSpace(distinguishedName))
                .Cast<string>()
                .OrderBy(distinguishedName => distinguishedName, StringComparer.OrdinalIgnoreCase)
                .ToArray());
    }

    private static AmbiguousDirectoryIdentityException CreateAmbiguousDirectoryIdentityException(
        string lookupKind,
        string lookupValue,
        string identityAttribute,
        IReadOnlyList<string> distinguishedNames)
    {
        return new AmbiguousDirectoryIdentityException(
            lookupKind,
            lookupValue,
            identityAttribute,
            distinguishedNames);
    }

    private static string ResolveIdentityAttribute(IReadOnlyList<(string Attribute, string Value)> searchClauses)
    {
        if (searchClauses.Count == 0)
        {
            return "identity attribute";
        }

        return string.Join(
            ", ",
            searchClauses
                .Select(clause => clause.Attribute)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(attribute => attribute, StringComparer.OrdinalIgnoreCase));
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

    private static string? GetAttribute(SearchResultEntry entry, string attributeName)
    {
        var resolvedAttributeName = ResolveAttributeName(entry, attributeName);
        if (resolvedAttributeName is null)
        {
            return null;
        }

        var attribute = entry.Attributes[resolvedAttributeName];
        return attribute.Count == 0 ? null : attribute[0]?.ToString();
    }

    private static string? ResolveAttributeName(SearchResultEntry entry, string attributeName)
    {
        if (entry.Attributes.Contains(attributeName))
        {
            return attributeName;
        }

        return entry.Attributes.AttributeNames
            .Cast<string>()
            .FirstOrDefault(candidate => string.Equals(candidate, attributeName, StringComparison.OrdinalIgnoreCase));
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

    private static bool ShouldRetryTransientLdapFailure(LdapException exception, int attempt)
    {
        return attempt < MaxTransientLdapRetries &&
               string.Equals(exception.Message, "The LDAP server is unavailable.", StringComparison.OrdinalIgnoreCase);
    }

    private static bool ShouldRetryTransientLdapFailure(InvalidOperationException exception, int attempt)
    {
        return attempt < MaxTransientLdapRetries &&
               ExternalSystemExceptionFactory.IsRetryableActiveDirectoryTimeout(exception);
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

    private static bool IsReferralException(DirectoryOperationException exception)
    {
        return exception.Response?.ResultCode == ResultCode.Referral ||
               exception.Message.Contains("referral", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsReferralException(LdapException exception)
    {
        return exception.Message.Contains("referral", StringComparison.OrdinalIgnoreCase);
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

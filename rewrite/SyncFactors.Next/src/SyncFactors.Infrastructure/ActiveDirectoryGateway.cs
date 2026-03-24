using SyncFactors.Contracts;
using SyncFactors.Domain;
using Microsoft.Extensions.Logging;
using System.DirectoryServices.Protocols;
using System.Net;

namespace SyncFactors.Infrastructure;

public sealed class ActiveDirectoryGateway(
    SyncFactorsConfigurationLoader configLoader,
    ScaffoldDirectoryGateway fallbackGateway,
    ILogger<ActiveDirectoryGateway> logger) : IDirectoryGateway
{
    public async Task<DirectoryUserSnapshot?> FindByWorkerAsync(WorkerSnapshot worker, CancellationToken cancellationToken)
    {
        var config = configLoader.GetSyncConfig().Ad;
        if (string.IsNullOrWhiteSpace(config.Server))
        {
            logger.LogWarning("AD server was not configured. Falling back to scaffold directory gateway. WorkerId={WorkerId}", worker.WorkerId);
            return await fallbackGateway.FindByWorkerAsync(worker, cancellationToken);
        }

        try
        {
            var directoryUser = await Task.Run(() => QueryDirectory(worker, config), cancellationToken);
            if (directoryUser is not null)
            {
                logger.LogInformation("Resolved directory user from AD. WorkerId={WorkerId} SamAccountName={SamAccountName}", worker.WorkerId, directoryUser.SamAccountName);
                return directoryUser;
            }

            logger.LogWarning("No AD user matched worker. Falling back to scaffold directory gateway. WorkerId={WorkerId}", worker.WorkerId);
            return await fallbackGateway.FindByWorkerAsync(worker, cancellationToken);
        }
        catch (LdapException ex)
        {
            logger.LogError(ex, "AD lookup failed with LDAP exception. Falling back to scaffold directory gateway. WorkerId={WorkerId} Server={Server}", worker.WorkerId, config.Server);
            return await fallbackGateway.FindByWorkerAsync(worker, cancellationToken);
        }
        catch (DirectoryOperationException ex)
        {
            logger.LogError(ex, "AD lookup failed with directory operation exception. Falling back to scaffold directory gateway. WorkerId={WorkerId} Server={Server}", worker.WorkerId, config.Server);
            return await fallbackGateway.FindByWorkerAsync(worker, cancellationToken);
        }
    }

    public async Task<string> ResolveAvailableEmailLocalPartAsync(WorkerSnapshot worker, CancellationToken cancellationToken)
    {
        var baseLocalPart = DirectoryIdentityFormatter.BuildBaseEmailLocalPart(worker.PreferredName, worker.LastName);
        var config = configLoader.GetSyncConfig().Ad;
        if (string.IsNullOrWhiteSpace(config.Server))
        {
            logger.LogWarning("AD server was not configured while resolving email local part. Returning base value. WorkerId={WorkerId}", worker.WorkerId);
            return baseLocalPart;
        }

        try
        {
            return await Task.Run(() => ResolveAvailableEmailLocalPart(worker, config, baseLocalPart), cancellationToken);
        }
        catch (LdapException ex)
        {
            logger.LogError(ex, "AD email local part lookup failed with LDAP exception. Returning base value. WorkerId={WorkerId} Server={Server}", worker.WorkerId, config.Server);
            return baseLocalPart;
        }
        catch (DirectoryOperationException ex)
        {
            logger.LogError(ex, "AD email local part lookup failed with directory operation exception. Returning base value. WorkerId={WorkerId} Server={Server}", worker.WorkerId, config.Server);
            return baseLocalPart;
        }
    }

    private static DirectoryUserSnapshot? QueryDirectory(WorkerSnapshot worker, ActiveDirectoryConfig config)
    {
        using var connection = CreateConnection(config);
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
            "title",
            "division",
            "employeeType",
            "extensionAttribute1",
            "extensionAttribute2",
            "extensionAttribute3",
            "extensionAttribute4");

        var response = (SearchResponse)connection.SendRequest(request);
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

    private static string ResolveAvailableEmailLocalPart(WorkerSnapshot worker, ActiveDirectoryConfig config, string baseLocalPart)
    {
        using var connection = CreateConnection(config);

        for (var suffix = 0; suffix < 1000; suffix++)
        {
            var candidate = suffix == 0 ? baseLocalPart : $"{baseLocalPart}{suffix + 1}";
            if (!EmailLocalPartExists(connection, config, candidate))
            {
                return candidate;
            }
        }

        throw new InvalidOperationException($"Could not find an available email local part for worker {worker.WorkerId}.");
    }

    private static bool EmailLocalPartExists(LdapConnection connection, ActiveDirectoryConfig config, string localPart)
    {
        var userPrincipalName = DirectoryIdentityFormatter.BuildEmailAddress(localPart);
        var request = new SearchRequest(
            config.DefaultActiveOu,
            $"(userPrincipalName={EscapeLdapFilter(userPrincipalName)})",
            SearchScope.Subtree,
            "sAMAccountName");

        var response = (SearchResponse)connection.SendRequest(request);
        return response.Entries.Count > 0;
    }

    private static LdapConnection CreateConnection(ActiveDirectoryConfig config)
    {
        var identifier = new LdapDirectoryIdentifier(config.Server);
        var connection = new LdapConnection(identifier)
        {
            AuthType = string.IsNullOrWhiteSpace(config.Username) ? AuthType.Anonymous : AuthType.Basic,
        };

        if (!string.IsNullOrWhiteSpace(config.Username))
        {
            connection.Credential = new NetworkCredential(config.Username, config.BindPassword);
        }

        connection.SessionOptions.ProtocolVersion = 3;
        connection.Bind();
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
            ["title"] = GetAttribute(entry, "title"),
            ["division"] = GetAttribute(entry, "division"),
            ["employeeType"] = GetAttribute(entry, "employeeType"),
            ["extensionAttribute1"] = GetAttribute(entry, "extensionAttribute1"),
            ["extensionAttribute2"] = GetAttribute(entry, "extensionAttribute2"),
            ["extensionAttribute3"] = GetAttribute(entry, "extensionAttribute3"),
            ["extensionAttribute4"] = GetAttribute(entry, "extensionAttribute4")
        };
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

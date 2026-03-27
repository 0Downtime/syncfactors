using SyncFactors.Contracts;
using SyncFactors.Domain;
using Microsoft.Extensions.Logging;
using System.DirectoryServices.Protocols;
using System.Net;

namespace SyncFactors.Infrastructure;

public sealed class ActiveDirectoryCommandGateway(
    SyncFactorsConfigurationLoader configLoader,
    ILogger<ActiveDirectoryCommandGateway> logger) : IDirectoryCommandGateway
{
    public async Task<DirectoryCommandResult> ExecuteAsync(DirectoryMutationCommand command, CancellationToken cancellationToken)
    {
        var config = configLoader.GetSyncConfig().Ad;
        if (string.IsNullOrWhiteSpace(config.Server))
        {
            throw new InvalidOperationException("AD server was not configured.");
        }

        try
        {
            logger.LogInformation("Executing AD command. Action={Action} WorkerId={WorkerId} SamAccountName={SamAccountName}", command.Action, command.WorkerId, command.SamAccountName);
            var result = await Task.Run(() => ExecuteCommand(command, config), cancellationToken);
            logger.LogInformation("AD command completed. Action={Action} WorkerId={WorkerId} Succeeded={Succeeded} Message={Message}", command.Action, command.WorkerId, result.Succeeded, result.Message);
            return result;
        }
        catch (LdapException ex)
        {
            logger.LogError(ex, "AD command failed with LDAP exception. Action={Action} WorkerId={WorkerId} Server={Server}", command.Action, command.WorkerId, config.Server);
            throw ExternalSystemExceptionFactory.CreateActiveDirectoryException($"command '{command.Action}'", config.Server, ex);
        }
        catch (DirectoryOperationException ex)
        {
            logger.LogError(ex, "AD command failed with directory operation exception. Action={Action} WorkerId={WorkerId} Server={Server}", command.Action, command.WorkerId, config.Server);
            throw ExternalSystemExceptionFactory.CreateActiveDirectoryException($"command '{command.Action}'", config.Server, ex);
        }
    }

    private static DirectoryCommandResult ExecuteCommand(DirectoryMutationCommand command, ActiveDirectoryConfig config)
    {
        using var connection = CreateConnection(config);

        return command.Action switch
        {
            "CreateUser" => CreateUser(connection, command, config),
            "UpdateUser" => UpdateUser(connection, command, config),
            _ => new DirectoryCommandResult(false, command.Action, command.SamAccountName, null, $"Unsupported action {command.Action}.", null)
        };
    }

    private static DirectoryCommandResult CreateUser(LdapConnection connection, DirectoryMutationCommand command, ActiveDirectoryConfig config)
    {
        var dn = $"CN={EscapeDnComponent(command.DisplayName)},{command.TargetOu}";
        var attributes = new List<DirectoryAttribute>
        {
            new("objectClass", "top", "person", "organizationalPerson", "user"),
            new("cn", command.DisplayName),
            new("displayName", command.DisplayName),
            new("sAMAccountName", command.SamAccountName),
            new("userPrincipalName", command.UserPrincipalName),
            new("mail", command.Mail),
            new(config.IdentityAttribute, command.WorkerId)
        };

        foreach (var attribute in command.Attributes)
        {
            if (string.IsNullOrWhiteSpace(attribute.Value) || IsReservedAttribute(attribute.Key, config.IdentityAttribute))
            {
                continue;
            }

            attributes.Add(new DirectoryAttribute(attribute.Key, attribute.Value));
        }

        var request = new AddRequest(dn, [.. attributes]);

        connection.SendRequest(request);
        var managerDn = ResolveManagerDistinguishedName(connection, command.ManagerId, config);
        if (!string.IsNullOrWhiteSpace(managerDn))
        {
            SetManager(connection, dn, managerDn);
        }

        return new DirectoryCommandResult(
            Succeeded: true,
            Action: command.Action,
            SamAccountName: command.SamAccountName,
            DistinguishedName: dn,
            Message: $"Created AD user {command.SamAccountName}.",
            RunId: null);
    }

    private static DirectoryCommandResult UpdateUser(LdapConnection connection, DirectoryMutationCommand command, ActiveDirectoryConfig config)
    {
        var existing = FindExistingUser(connection, command.WorkerId, config);
        if (existing is null)
        {
            return new DirectoryCommandResult(
                Succeeded: false,
                Action: command.Action,
                SamAccountName: command.SamAccountName,
                DistinguishedName: null,
                Message: $"Could not find AD user for worker {command.WorkerId}.",
                RunId: null);
        }

        var distinguishedName = existing.DistinguishedName;
        if (string.IsNullOrWhiteSpace(distinguishedName))
        {
            return new DirectoryCommandResult(false, command.Action, command.SamAccountName, null, "Existing AD user did not include a distinguished name.", null);
        }

        var request = new ModifyRequest(distinguishedName);
        request.Modifications.Add(BuildReplaceModification("displayName", command.DisplayName));
        request.Modifications.Add(BuildReplaceModification("userPrincipalName", command.UserPrincipalName));
        request.Modifications.Add(BuildReplaceModification("mail", command.Mail));
        request.Modifications.Add(BuildReplaceModification(config.IdentityAttribute, command.WorkerId));

        foreach (var attribute in command.Attributes)
        {
            if (IsReservedAttribute(attribute.Key, config.IdentityAttribute))
            {
                continue;
            }

            request.Modifications.Add(
                string.IsNullOrWhiteSpace(attribute.Value)
                    ? BuildDeleteModification(attribute.Key)
                    : BuildReplaceModification(attribute.Key, attribute.Value));
        }

        var managerDn = ResolveManagerDistinguishedName(connection, command.ManagerId, config);
        if (!string.IsNullOrWhiteSpace(managerDn))
        {
            request.Modifications.Add(BuildReplaceModification("manager", managerDn));
        }

        connection.SendRequest(request);
        return new DirectoryCommandResult(
            Succeeded: true,
            Action: command.Action,
            SamAccountName: command.SamAccountName,
            DistinguishedName: distinguishedName,
            Message: $"Updated AD user {command.SamAccountName}.",
            RunId: null);
    }

    private static DirectoryUserSnapshot? FindExistingUser(LdapConnection connection, string workerId, ActiveDirectoryConfig config)
    {
        var request = new SearchRequest(
            config.DefaultActiveOu,
            $"({EscapeLdapFilter(config.IdentityAttribute)}={EscapeLdapFilter(workerId)})",
            SearchScope.Subtree,
            "sAMAccountName",
            "distinguishedName",
            "displayName");

        var response = (SearchResponse)connection.SendRequest(request);
        var entry = response.Entries.Cast<SearchResultEntry>().FirstOrDefault();
        if (entry is null)
        {
            return null;
        }

        return new DirectoryUserSnapshot(
            SamAccountName: GetAttribute(entry, "sAMAccountName"),
            DistinguishedName: GetAttribute(entry, "distinguishedName"),
            Enabled: null,
            DisplayName: GetAttribute(entry, "displayName"),
            Attributes: new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
            {
                ["displayName"] = GetAttribute(entry, "displayName")
            });
    }

    private static string? ResolveManagerDistinguishedName(LdapConnection connection, string? managerId, ActiveDirectoryConfig config)
    {
        if (string.IsNullOrWhiteSpace(managerId))
        {
            return null;
        }

        var request = new SearchRequest(
            config.DefaultActiveOu,
            $"({EscapeLdapFilter(config.IdentityAttribute)}={EscapeLdapFilter(managerId)})",
            SearchScope.Subtree,
            "distinguishedName");

        var response = (SearchResponse)connection.SendRequest(request);
        var entry = response.Entries.Cast<SearchResultEntry>().FirstOrDefault();
        return entry is null ? null : GetAttribute(entry, "distinguishedName");
    }

    private static void SetManager(LdapConnection connection, string distinguishedName, string managerDn)
    {
        var request = new ModifyRequest(distinguishedName);
        request.Modifications.Add(BuildReplaceModification("manager", managerDn));
        connection.SendRequest(request);
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

    private static string EscapeLdapFilter(string value)
    {
        return value
            .Replace("\\", "\\5c", StringComparison.Ordinal)
            .Replace("*", "\\2a", StringComparison.Ordinal)
            .Replace("(", "\\28", StringComparison.Ordinal)
            .Replace(")", "\\29", StringComparison.Ordinal)
            .Replace("\0", "\\00", StringComparison.Ordinal);
    }

    private static string EscapeDnComponent(string value)
    {
        return value.Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace(",", "\\,", StringComparison.Ordinal)
            .Replace("+", "\\+", StringComparison.Ordinal)
            .Replace("\"", "\\\"", StringComparison.Ordinal)
            .Replace("<", "\\<", StringComparison.Ordinal)
            .Replace(">", "\\>", StringComparison.Ordinal)
            .Replace(";", "\\;", StringComparison.Ordinal);
    }

    private static DirectoryAttributeModification BuildReplaceModification(string attributeName, string value)
    {
        var modification = new DirectoryAttributeModification
        {
            Name = attributeName,
            Operation = DirectoryAttributeOperation.Replace
        };
        modification.Add(value);
        return modification;
    }

    private static DirectoryAttributeModification BuildDeleteModification(string attributeName)
    {
        return new DirectoryAttributeModification
        {
            Name = attributeName,
            Operation = DirectoryAttributeOperation.Delete
        };
    }

    private static bool IsReservedAttribute(string attributeName, string identityAttribute)
    {
        return string.Equals(attributeName, "objectClass", StringComparison.OrdinalIgnoreCase)
            || string.Equals(attributeName, "cn", StringComparison.OrdinalIgnoreCase)
            || string.Equals(attributeName, "displayName", StringComparison.OrdinalIgnoreCase)
            || string.Equals(attributeName, "sAMAccountName", StringComparison.OrdinalIgnoreCase)
            || string.Equals(attributeName, "userPrincipalName", StringComparison.OrdinalIgnoreCase)
            || string.Equals(attributeName, "mail", StringComparison.OrdinalIgnoreCase)
            || string.Equals(attributeName, "manager", StringComparison.OrdinalIgnoreCase)
            || string.Equals(attributeName, identityAttribute, StringComparison.OrdinalIgnoreCase);
    }
}

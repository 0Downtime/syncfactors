using SyncFactors.Contracts;
using SyncFactors.Domain;
using Microsoft.Extensions.Logging;
using System.DirectoryServices.Protocols;
using System.Net;

namespace SyncFactors.Infrastructure;

public sealed class ActiveDirectoryCommandGateway(
    SyncFactorsConfigurationLoader configLoader,
    ScaffoldDirectoryCommandGateway fallbackGateway,
    ILogger<ActiveDirectoryCommandGateway> logger) : IDirectoryCommandGateway
{
    public async Task<DirectoryCommandResult> ExecuteAsync(DirectoryMutationCommand command, CancellationToken cancellationToken)
    {
        var config = configLoader.GetSyncConfig().Ad;
        if (string.IsNullOrWhiteSpace(config.Server))
        {
            logger.LogWarning("AD command gateway server was not configured. Falling back to scaffold command gateway. Action={Action} WorkerId={WorkerId}", command.Action, command.WorkerId);
            return await fallbackGateway.ExecuteAsync(command, cancellationToken);
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
            logger.LogError(ex, "AD command failed with LDAP exception. Falling back to scaffold command gateway. Action={Action} WorkerId={WorkerId} Server={Server}", command.Action, command.WorkerId, config.Server);
            return await fallbackGateway.ExecuteAsync(command, cancellationToken);
        }
        catch (DirectoryOperationException ex)
        {
            logger.LogError(ex, "AD command failed with directory operation exception. Falling back to scaffold command gateway. Action={Action} WorkerId={WorkerId} Server={Server}", command.Action, command.WorkerId, config.Server);
            return await fallbackGateway.ExecuteAsync(command, cancellationToken);
        }
    }

    private static DirectoryCommandResult ExecuteCommand(DirectoryMutationCommand command, ActiveDirectoryConfig config)
    {
        using var connection = CreateConnection(config);

        return command.Action switch
        {
            "CreateUser" => CreateUser(connection, command),
            "UpdateUser" => UpdateUser(connection, command, config),
            _ => new DirectoryCommandResult(false, command.Action, command.SamAccountName, null, $"Unsupported action {command.Action}.", null)
        };
    }

    private static DirectoryCommandResult CreateUser(LdapConnection connection, DirectoryMutationCommand command)
    {
        var dn = $"CN={EscapeDnComponent(command.DisplayName)},{command.TargetOu}";
        var request = new AddRequest(
            dn,
            new DirectoryAttribute("objectClass", "top", "person", "organizationalPerson", "user"),
            new DirectoryAttribute("cn", command.DisplayName),
            new DirectoryAttribute("displayName", command.DisplayName),
            new DirectoryAttribute("sAMAccountName", command.SamAccountName),
            new DirectoryAttribute("userPrincipalName", $"{command.SamAccountName}@example.com"));

        connection.SendRequest(request);
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

        var request = new ModifyRequest(
            distinguishedName,
            new DirectoryAttributeModification
            {
                Name = "displayName",
                Operation = DirectoryAttributeOperation.Replace
            });
        ((DirectoryAttributeModification)request.Modifications[0]).Add(command.DisplayName);

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
            DisplayName: GetAttribute(entry, "displayName"));
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
}

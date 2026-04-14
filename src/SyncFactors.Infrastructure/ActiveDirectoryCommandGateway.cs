using SyncFactors.Contracts;
using SyncFactors.Domain;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using System.DirectoryServices.Protocols;
using System.Diagnostics;

namespace SyncFactors.Infrastructure;

public sealed class ActiveDirectoryCommandGateway(
    SyncFactorsConfigurationLoader configLoader,
    ILogger<ActiveDirectoryCommandGateway> logger) : IDirectoryCommandGateway
{
    private static readonly TimeSpan LdapOperationTimeout = TimeSpan.FromSeconds(10);

    private static readonly IReadOnlyDictionary<string, string> AttributeAliases =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["GivenName"] = "givenName",
            ["Surname"] = "sn",
            ["UserPrincipalName"] = "userPrincipalName",
            ["Office"] = "physicalDeliveryOfficeName"
        };

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
            var result = await ExecuteWithTimeoutAsync(
                operation: () => ExecuteCommand(command, config, logger),
                operationName: $"command '{command.Action}'",
                server: config.Server,
                cancellationToken: cancellationToken);
            logger.LogInformation("AD command completed. Action={Action} WorkerId={WorkerId} Succeeded={Succeeded} Message={Message}", command.Action, command.WorkerId, result.Succeeded, result.Message);
            return result;
        }
        catch (LdapException ex)
        {
            logger.LogError(ex, "AD command failed with LDAP exception. Action={Action} WorkerId={WorkerId} Server={Server}", command.Action, command.WorkerId, config.Server);
            throw ExternalSystemExceptionFactory.CreateActiveDirectoryException($"command '{command.Action}'", config, ex);
        }
        catch (DirectoryOperationException ex)
        {
            logger.LogError(ex, "AD command failed with directory operation exception. Action={Action} WorkerId={WorkerId} Server={Server}", command.Action, command.WorkerId, config.Server);
            throw ExternalSystemExceptionFactory.CreateActiveDirectoryException($"command '{command.Action}'", config, ex);
        }
    }

    private static DirectoryCommandResult ExecuteCommand(DirectoryMutationCommand command, ActiveDirectoryConfig config, ILogger logger)
    {
        using var connection = CreateConnection(config, logger);
        var operations = command.Operations.Count > 0
            ? command.Operations
            : [new SyncFactors.Contracts.DirectoryOperation(command.Action, command.TargetOu)];
        string? distinguishedName = command.CurrentDistinguishedName;

        foreach (var operation in operations)
        {
            distinguishedName = operation.Kind switch
            {
                "CreateUser" => CreateUser(connection, command, config, logger).DistinguishedName,
                "UpdateUser" => UpdateUser(connection, command, config, logger, distinguishedName).DistinguishedName,
                "MoveUser" => MoveUser(connection, command, config, logger, operation.TargetOu, distinguishedName).DistinguishedName,
                "EnableUser" => SetAccountEnabled(connection, command, config, logger, true, distinguishedName).DistinguishedName,
                "DisableUser" => SetAccountEnabled(connection, command, config, logger, false, distinguishedName).DistinguishedName,
                "DeleteUser" => DeleteUser(connection, command, config, logger, distinguishedName).DistinguishedName,
                _ => throw new InvalidOperationException($"Unsupported action {operation.Kind}.")
            };
        }

        return new DirectoryCommandResult(
            Succeeded: true,
            Action: command.Action,
            SamAccountName: command.SamAccountName,
            DistinguishedName: distinguishedName,
            Message: BuildCompletionMessage(command, operations),
            RunId: null);
    }

    private static DirectoryCommandResult CreateUser(LdapConnection connection, DirectoryMutationCommand command, ActiveDirectoryConfig config, ILogger logger)
    {
        var dn = $"CN={EscapeDnComponent(command.CommonName)},{command.TargetOu}";
        var attributes = BuildCreateAttributes(command, config);
        var request = new AddRequest(dn, [.. attributes]);
        LogRequestAttributes("CreateUser", command.WorkerId, attributes, logger);

        ExecuteModify(connection, request, logger, "create user add request", ("WorkerId", command.WorkerId), ("SamAccountName", command.SamAccountName));
        var managerDn = command.ManagerDistinguishedName
            ?? ResolveManagerDistinguishedName(connection, command.ManagerId, config);
        if (!string.IsNullOrWhiteSpace(managerDn))
        {
            SetManager(connection, dn, managerDn, logger, command.WorkerId);
        }

        return new DirectoryCommandResult(true, command.Action, command.SamAccountName, dn, $"Created AD user {command.SamAccountName}.", null);
    }

    private static DirectoryCommandResult UpdateUser(LdapConnection connection, DirectoryMutationCommand command, ActiveDirectoryConfig config, ILogger logger, string? distinguishedName)
    {
        string step = "FindExistingUser";
        DirectoryUserSnapshot? existing = null;
        string? currentCn = null;
        DirectoryAttributeModificationCollection? modifications = null;

        try
        {
            existing = FindExistingUser(connection, command.WorkerId, config);
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

            distinguishedName ??= existing.DistinguishedName;
            if (string.IsNullOrWhiteSpace(distinguishedName))
            {
                return new DirectoryCommandResult(false, command.Action, command.SamAccountName, null, "Existing AD user did not include a distinguished name.", null);
            }

            currentCn = existing.Attributes.TryGetValue("cn", out var resolvedCn) ? resolvedCn : null;
            if (!string.Equals(currentCn, command.CommonName, StringComparison.Ordinal))
            {
                step = "RenameUser";
                try
                {
                    RenameUser(connection, distinguishedName, command.CommonName, logger, command.WorkerId);
                    distinguishedName = BuildRenamedDistinguishedName(distinguishedName, command.CommonName);
                }
                catch (Exception ex) when (ex is LdapException or DirectoryOperationException)
                {
                    throw ExternalSystemExceptionFactory.CreateActiveDirectoryException(
                        "command 'UpdateUser'",
                        config,
                        ex,
                        BuildUpdateRenameFailureDetails(command, distinguishedName, currentCn));
                }
            }

            step = "BuildModifyRequest";
            step = "ResolveManager";
            var managerDn = command.ManagerDistinguishedName
                ?? ResolveManagerDistinguishedName(connection, command.ManagerId, config);
            var request = new ModifyRequest(distinguishedName);
            foreach (DirectoryAttributeModification modification in BuildUpdateModifications(command, config, managerDn))
            {
                request.Modifications.Add(modification);
            }

            modifications = request.Modifications;

            step = "ModifyAttributes";
            LogRequestModifications("UpdateUser", command.WorkerId, request.Modifications, logger);
            try
            {
                ExecuteModify(connection, request, logger, "update user modify request", ("WorkerId", command.WorkerId), ("SamAccountName", command.SamAccountName));
            }
            catch (Exception ex) when (ex is LdapException or DirectoryOperationException)
            {
                throw ExternalSystemExceptionFactory.CreateActiveDirectoryException(
                    "command 'UpdateUser'",
                    config,
                    ex,
                    BuildUpdateModifyFailureDetails(command, distinguishedName, request.Modifications));
            }

            return new DirectoryCommandResult(true, command.Action, command.SamAccountName, distinguishedName, $"Updated AD user {command.SamAccountName}.", null);
        }
        catch (Exception ex) when (ex is LdapException or DirectoryOperationException)
        {
            throw ExternalSystemExceptionFactory.CreateActiveDirectoryException(
                "command 'UpdateUser'",
                config,
                ex,
                BuildUpdateStepFailureDetails(command, distinguishedName, currentCn, modifications, step));
        }
    }

    private static DirectoryUserSnapshot? FindExistingUser(LdapConnection connection, string workerId, ActiveDirectoryConfig config)
    {
        var entry = FindFirstEntry(
            connection,
            GetSearchBases(config),
            $"({EscapeLdapFilter(config.IdentityAttribute)}={EscapeLdapFilter(workerId)})",
            NullLogger<ActiveDirectoryCommandGateway>.Instance,
            "find existing user search",
            ("WorkerId", workerId),
            ("IdentityAttribute", config.IdentityAttribute));
        if (entry is null)
        {
            return null;
        }

        return new DirectoryUserSnapshot(
            SamAccountName: GetAttribute(entry, "sAMAccountName"),
            DistinguishedName: GetAttribute(entry, "distinguishedName"),
            Enabled: ParseEnabled(GetAttribute(entry, "userAccountControl")),
            DisplayName: GetAttribute(entry, "displayName"),
            Attributes: new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
            {
                ["cn"] = GetAttribute(entry, "cn"),
                ["displayName"] = GetAttribute(entry, "displayName"),
                ["userAccountControl"] = GetAttribute(entry, "userAccountControl")
            });
    }

    private static string? ResolveManagerDistinguishedName(LdapConnection connection, string? managerId, ActiveDirectoryConfig config, ILogger? logger = null)
    {
        if (string.IsNullOrWhiteSpace(managerId))
        {
            return null;
        }

        var entry = FindFirstEntry(
            connection,
            GetSearchBases(config),
            $"({EscapeLdapFilter(config.IdentityAttribute)}={EscapeLdapFilter(managerId)})",
            logger ?? NullLogger<ActiveDirectoryCommandGateway>.Instance,
            "command manager DN search",
            ("ManagerId", managerId),
            ("IdentityAttribute", config.IdentityAttribute));
        return entry is null ? null : GetAttribute(entry, "distinguishedName");
    }

    private static DirectoryCommandResult MoveUser(
        LdapConnection connection,
        DirectoryMutationCommand command,
        ActiveDirectoryConfig config,
        ILogger logger,
        string? targetOu,
        string? distinguishedName)
    {
        var existing = FindExistingUser(connection, command.WorkerId, config);
        distinguishedName ??= existing?.DistinguishedName;
        if (string.IsNullOrWhiteSpace(distinguishedName) || string.IsNullOrWhiteSpace(targetOu))
        {
            return new DirectoryCommandResult(false, command.Action, command.SamAccountName, distinguishedName, "Could not resolve move target for AD user.", null);
        }

        var currentRdn = GetRelativeDistinguishedName(distinguishedName);
        var request = new ModifyDNRequest(distinguishedName, targetOu, currentRdn) { DeleteOldRdn = true };
        ExecuteModify(connection, request, logger, "move user modify-dn request", ("WorkerId", command.WorkerId), ("TargetOu", targetOu));
        var movedDn = $"{currentRdn},{targetOu}";
        return new DirectoryCommandResult(true, command.Action, command.SamAccountName, movedDn, $"Moved AD user {command.SamAccountName}.", null);
    }

    private static DirectoryCommandResult SetAccountEnabled(
        LdapConnection connection,
        DirectoryMutationCommand command,
        ActiveDirectoryConfig config,
        ILogger logger,
        bool enabled,
        string? distinguishedName)
    {
        var existing = FindExistingUser(connection, command.WorkerId, config);
        distinguishedName ??= existing?.DistinguishedName;
        if (string.IsNullOrWhiteSpace(distinguishedName))
        {
            return new DirectoryCommandResult(false, command.Action, command.SamAccountName, null, "Could not resolve AD user to update account state.", null);
        }

        const int AccountDisabledFlag = 0x0002;
        var currentValue = ResolveUserAccountControl(existing);
        var targetValue = enabled
            ? currentValue & ~AccountDisabledFlag
            : currentValue | AccountDisabledFlag;
        var request = new ModifyRequest(distinguishedName);
        request.Modifications.Add(BuildReplaceModification("userAccountControl", targetValue.ToString()));
        ExecuteModify(connection, request, logger, enabled ? "enable user modify request" : "disable user modify request", ("WorkerId", command.WorkerId));
        return new DirectoryCommandResult(true, command.Action, command.SamAccountName, distinguishedName, enabled ? $"Enabled AD user {command.SamAccountName}." : $"Disabled AD user {command.SamAccountName}.", null);
    }

    private static DirectoryCommandResult DeleteUser(
        LdapConnection connection,
        DirectoryMutationCommand command,
        ActiveDirectoryConfig config,
        ILogger logger,
        string? distinguishedName)
    {
        var existing = FindExistingUser(connection, command.WorkerId, config);
        distinguishedName ??= existing?.DistinguishedName;
        if (string.IsNullOrWhiteSpace(distinguishedName))
        {
            return new DirectoryCommandResult(false, command.Action, command.SamAccountName, null, "Could not resolve AD user to delete.", null);
        }

        var request = new DeleteRequest(distinguishedName);
        ExecuteModify(connection, request, logger, "delete user request", ("WorkerId", command.WorkerId), ("DistinguishedName", distinguishedName));
        return new DirectoryCommandResult(true, command.Action, command.SamAccountName, distinguishedName, $"Deleted AD user {command.SamAccountName}.", null);
    }

    private static void SetManager(LdapConnection connection, string distinguishedName, string managerDn, ILogger logger, string workerId)
    {
        var request = new ModifyRequest(distinguishedName);
        request.Modifications.Add(BuildReplaceModification("manager", managerDn));
        ExecuteModify(connection, request, logger, "set manager modify request", ("WorkerId", workerId), ("DistinguishedName", distinguishedName));
    }

    private static void RenameUser(LdapConnection connection, string distinguishedName, string commonName, ILogger logger, string workerId)
    {
        var parentDistinguishedName = GetParentDistinguishedName(distinguishedName);
        var request = new ModifyDNRequest(distinguishedName, parentDistinguishedName, $"CN={EscapeDnComponent(commonName)}")
        {
            DeleteOldRdn = true
        };

        ExecuteModify(
            connection,
            request,
            logger,
            "rename user modify-dn request",
            ("WorkerId", workerId),
            ("DistinguishedName", distinguishedName),
            ("NewCommonName", commonName));
    }

    private static string BuildRenamedDistinguishedName(string distinguishedName, string commonName)
    {
        var parentDistinguishedName = GetParentDistinguishedName(distinguishedName);
        return $"CN={EscapeDnComponent(commonName)},{parentDistinguishedName}";
    }

    private static string GetParentDistinguishedName(string distinguishedName)
    {
        var separatorIndex = FindFirstUnescapedComma(distinguishedName);
        if (separatorIndex < 0 || separatorIndex == distinguishedName.Length - 1)
        {
            throw new InvalidOperationException($"Could not resolve parent distinguished name from '{distinguishedName}'.");
        }

        return distinguishedName[(separatorIndex + 1)..];
    }

    private static string GetRelativeDistinguishedName(string distinguishedName)
    {
        var separatorIndex = FindFirstUnescapedComma(distinguishedName);
        if (separatorIndex < 0)
        {
            throw new InvalidOperationException($"Could not resolve relative distinguished name from '{distinguishedName}'.");
        }

        return distinguishedName[..separatorIndex];
    }

    private static int FindFirstUnescapedComma(string distinguishedName)
    {
        for (var index = 0; index < distinguishedName.Length; index++)
        {
            if (distinguishedName[index] != ',')
            {
                continue;
            }

            var backslashCount = 0;
            for (var lookbehind = index - 1; lookbehind >= 0 && distinguishedName[lookbehind] == '\\'; lookbehind--)
            {
                backslashCount++;
            }

            if (backslashCount % 2 == 0)
            {
                return index;
            }
        }

        return -1;
    }

    private static LdapConnection CreateConnection(ActiveDirectoryConfig config, ILogger logger)
        => ActiveDirectoryConnectionFactory.CreateConnection(config, logger, LdapOperationTimeout);

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
        logger.LogInformation("Starting AD search. Operation={Operation} Context={Context}", operation, FormatContext(context));
        var response = (SearchResponse)connection.SendRequest(request);
        logger.LogInformation(
            "Completed AD search. Operation={Operation} DurationMs={DurationMs} Entries={Entries} Context={Context}",
            operation,
            stopwatch.ElapsedMilliseconds,
            response.Entries.Count,
            FormatContext(context));
        return response;
    }

    private static void ExecuteModify(
        LdapConnection connection,
        DirectoryRequest request,
        ILogger logger,
        string operation,
        params (string Key, object? Value)[] context)
    {
        var stopwatch = Stopwatch.StartNew();
        logger.LogInformation("Starting AD modify. Operation={Operation} Context={Context}", operation, FormatContext(context));
        connection.SendRequest(request);
        logger.LogInformation(
            "Completed AD modify. Operation={Operation} DurationMs={DurationMs} Context={Context}",
            operation,
            stopwatch.ElapsedMilliseconds,
            FormatContext(context));
    }

    private static string FormatContext((string Key, object? Value)[] context)
    {
        return string.Join(", ", context.Select(item => $"{item.Key}={item.Value ?? "(null)"}"));
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

    private static List<DirectoryAttribute> BuildCreateAttributes(DirectoryMutationCommand command, ActiveDirectoryConfig config)
    {
        var attributes = new List<DirectoryAttribute>
        {
            new("objectClass", "top", "person", "organizationalPerson", "user"),
            new("cn", command.CommonName),
            new("displayName", command.DisplayName),
            new("sAMAccountName", command.SamAccountName),
            new("userPrincipalName", command.UserPrincipalName),
            new("mail", command.Mail)
        };

        if (!string.IsNullOrWhiteSpace(command.WorkerId))
        {
            attributes.Add(new DirectoryAttribute(config.IdentityAttribute, command.WorkerId));
        }

        foreach (var attribute in command.Attributes)
        {
            var attributeName = NormalizeAttributeName(attribute.Key);
            if (string.IsNullOrWhiteSpace(attribute.Value) ||
                IsSystemManagedAttribute(attributeName) ||
                string.Equals(attributeName, config.IdentityAttribute, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            attributes.Add(new DirectoryAttribute(attributeName, attribute.Value));
        }

        return attributes;
    }

    private static DirectoryAttributeModificationCollection BuildUpdateModifications(
        DirectoryMutationCommand command,
        ActiveDirectoryConfig config,
        string? managerDn)
    {
        var modifications = new DirectoryAttributeModificationCollection
        {
            BuildReplaceModification("displayName", command.DisplayName),
            BuildReplaceModification("userPrincipalName", command.UserPrincipalName),
            BuildReplaceModification("mail", command.Mail)
        };

        if (!string.IsNullOrWhiteSpace(command.WorkerId))
        {
            modifications.Add(BuildReplaceModification(config.IdentityAttribute, command.WorkerId));
        }

        foreach (var attribute in command.Attributes)
        {
            var attributeName = NormalizeAttributeName(attribute.Key);
            if (IsSystemManagedAttribute(attributeName) ||
                string.Equals(attributeName, config.IdentityAttribute, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            modifications.Add(
                string.IsNullOrWhiteSpace(attribute.Value)
                    ? BuildDeleteModification(attributeName)
                    : BuildReplaceModification(attributeName, attribute.Value));
        }

        if (!string.IsNullOrWhiteSpace(managerDn))
        {
            modifications.Add(BuildReplaceModification("manager", managerDn));
        }

        return modifications;
    }

    private static bool IsSystemManagedAttribute(string attributeName)
    {
        return string.Equals(attributeName, "objectClass", StringComparison.OrdinalIgnoreCase)
            || string.Equals(attributeName, "cn", StringComparison.OrdinalIgnoreCase)
            || string.Equals(attributeName, "displayName", StringComparison.OrdinalIgnoreCase)
            || string.Equals(attributeName, "sAMAccountName", StringComparison.OrdinalIgnoreCase)
            || string.Equals(attributeName, "userPrincipalName", StringComparison.OrdinalIgnoreCase)
            || string.Equals(attributeName, "mail", StringComparison.OrdinalIgnoreCase)
            || string.Equals(attributeName, "manager", StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizeAttributeName(string attributeName)
    {
        return AttributeAliases.TryGetValue(attributeName, out var normalized)
            ? normalized
            : attributeName;
    }

    private static SearchResultEntry? FindFirstEntry(
        LdapConnection connection,
        IReadOnlyList<string> searchBases,
        string filter,
        ILogger logger,
        string operation,
        params (string Key, object? Value)[] context)
    {
        foreach (var searchBase in searchBases)
        {
            var request = new SearchRequest(
                searchBase,
                filter,
                SearchScope.Subtree,
                "sAMAccountName",
                "cn",
                "distinguishedName",
                "displayName",
                "userAccountControl");
            SearchResponse response;
            try
            {
                response = ExecuteSearch(connection, request, logger, operation, [.. context, ("SearchBase", searchBase)]);
            }
            catch (DirectoryOperationException ex) when (IsReferralException(ex))
            {
                logger.LogWarning(ex, "Skipping AD command search base because the server returned a referral. SearchBase={SearchBase} Operation={Operation}", searchBase, operation);
                continue;
            }
            catch (LdapException ex) when (IsReferralException(ex))
            {
                logger.LogWarning(ex, "Skipping AD command search base because the LDAP client encountered a referral. SearchBase={SearchBase} Operation={Operation}", searchBase, operation);
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

    private static IReadOnlyList<string> GetSearchBases(ActiveDirectoryConfig config)
    {
        return new[] { config.DefaultActiveOu, config.PrehireOu, config.GraveyardOu, config.LeaveOu }
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static int ResolveUserAccountControl(DirectoryUserSnapshot? existing)
    {
        if (existing?.Attributes.TryGetValue("userAccountControl", out var rawValue) == true &&
            int.TryParse(rawValue, out var parsedValue))
        {
            return parsedValue;
        }

        if (existing?.Enabled == false)
        {
            return 0x0202;
        }

        return 0x0200;
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

    private static string BuildCompletionMessage(DirectoryMutationCommand command, IReadOnlyList<SyncFactors.Contracts.DirectoryOperation> operations)
    {
        if (operations.Count == 1)
        {
            return $"{operations[0].Kind} completed for {command.SamAccountName}.";
        }

        return $"{string.Join(", ", operations.Select(operation => operation.Kind))} completed for {command.SamAccountName}.";
    }

    private static void LogRequestAttributes(string action, string workerId, IEnumerable<DirectoryAttribute> attributes, ILogger logger)
    {
        logger.LogInformation(
            "Prepared AD {Action} attribute payload. WorkerId={WorkerId} Attributes={Attributes}",
            action,
            workerId,
            string.Join(", ", attributes.Select(attribute => attribute.Name)));
    }

    private static void LogRequestModifications(string action, string workerId, DirectoryAttributeModificationCollection modifications, ILogger logger)
    {
        logger.LogInformation(
            "Prepared AD {Action} modification payload. WorkerId={WorkerId} Attributes={Attributes}",
            action,
            workerId,
            string.Join(", ", modifications.Cast<DirectoryAttributeModification>().Select(modification => modification.Name)));
    }

    private static string BuildUpdateRenameFailureDetails(DirectoryMutationCommand command, string distinguishedName, string? currentCn)
    {
        return $"Step=RenameUser WorkerId={command.WorkerId} SamAccountName={command.SamAccountName} DistinguishedName={distinguishedName} CurrentCn={FormatDetailValue(currentCn)} DesiredCn={FormatDetailValue(command.CommonName)}";
    }

    private static string BuildUpdateModifyFailureDetails(DirectoryMutationCommand command, string distinguishedName, DirectoryAttributeModificationCollection modifications)
    {
        return $"Step=ModifyAttributes WorkerId={command.WorkerId} SamAccountName={command.SamAccountName} DistinguishedName={distinguishedName} Attributes={FormatModificationAttributeNames(modifications)} ManagerId={FormatDetailValue(command.ManagerId)}";
    }

    private static string BuildUpdateStepFailureDetails(
        DirectoryMutationCommand command,
        string? distinguishedName,
        string? currentCn,
        DirectoryAttributeModificationCollection? modifications,
        string step)
    {
        return $"Step={step} WorkerId={command.WorkerId} SamAccountName={command.SamAccountName} DistinguishedName={FormatDetailValue(distinguishedName)} CurrentCn={FormatDetailValue(currentCn)} DesiredCn={FormatDetailValue(command.CommonName)} Attributes={FormatModificationAttributeNames(modifications)} ManagerId={FormatDetailValue(command.ManagerId)}";
    }

    private static string FormatModificationAttributeNames(DirectoryAttributeModificationCollection? modifications)
    {
        if (modifications is null || modifications.Count == 0)
        {
            return "(none)";
        }

        return string.Join(",", modifications.Cast<DirectoryAttributeModification>().Select(modification => modification.Name));
    }

    private static string FormatDetailValue(string? value)
    {
        return string.IsNullOrWhiteSpace(value) ? "(unset)" : value;
    }
}

using SyncFactors.Contracts;
using Microsoft.Extensions.Logging;

namespace SyncFactors.Domain;

public sealed class IdentityMatcher : IIdentityMatcher
{
    public IdentityMatchResult Match(WorkerSnapshot worker, DirectoryUserSnapshot? directoryUser)
    {
        if (!string.IsNullOrWhiteSpace(directoryUser?.SamAccountName))
        {
            return new IdentityMatchResult(
                Bucket: "updates",
                MatchedExistingUser: true,
                SamAccountName: directoryUser.SamAccountName!,
                Reason: "Native preview matched an existing directory account.",
                OperatorActionSummary: "Update account preview");
        }

        return new IdentityMatchResult(
            Bucket: "creates",
            MatchedExistingUser: false,
            SamAccountName: worker.WorkerId,
            Reason: "Native preview planned a new directory account.",
            OperatorActionSummary: "Create account preview");
    }
}

public sealed class AttributeDiffService : IAttributeDiffService
{
    private const string DisplayNameAttribute = "displayName";
    private const string UnsetValue = "(unset)";
    private const string SamAccountNameAttribute = "sAMAccountName";
    private const string UserPrincipalNameAttribute = "UserPrincipalName";
    private const string ProxyAddressesAttribute = "proxyAddresses";

    private readonly IAttributeMappingProvider _mappingProvider;
    private readonly ILogger<AttributeDiffService> _logger;
    private readonly IWorkerPreviewLogWriter _logWriter;
    private readonly IEmailAddressPolicy _emailAddressPolicy;

    public AttributeDiffService(
        IAttributeMappingProvider mappingProvider,
        IWorkerPreviewLogWriter logWriter,
        ILogger<AttributeDiffService> logger,
        IEmailAddressPolicy? emailAddressPolicy = null)
    {
        _mappingProvider = mappingProvider;
        _logWriter = logWriter;
        _logger = logger;
        _emailAddressPolicy = emailAddressPolicy ?? new DefaultEmailAddressPolicy();
    }

    public async Task<IReadOnlyList<AttributeChange>> BuildDiffAsync(
        WorkerSnapshot worker,
        DirectoryUserSnapshot? directoryUser,
        string? proposedEmailAddress,
        string? logPath,
        CancellationToken cancellationToken)
    {
        var currentAttributes = directoryUser?.Attributes is null
            ? new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
            : new Dictionary<string, string?>(directoryUser.Attributes, StringComparer.OrdinalIgnoreCase);
        if (!currentAttributes.ContainsKey(DisplayNameAttribute) && !string.IsNullOrWhiteSpace(directoryUser?.DisplayName))
        {
            currentAttributes[DisplayNameAttribute] = directoryUser.DisplayName;
        }

        var enabledMappings = _mappingProvider.GetEnabledMappings();
        if (!string.IsNullOrWhiteSpace(logPath))
        {
            await _logWriter.AppendAsync(logPath, new WorkerPreviewLogEntry(
                Event: "preview.diff.start",
                WorkerId: worker.WorkerId,
                Timestamp: DateTimeOffset.UtcNow,
                Target: null,
                Source: null,
                SourceValue: null,
                CurrentValue: null,
                ProposedValue: null,
                Changed: null,
                Message: $"Evaluating {enabledMappings.Count} enabled mappings."), cancellationToken);
        }

        var changes = new List<AttributeChange>();
        var proposedSamAccountName = string.IsNullOrWhiteSpace(directoryUser?.SamAccountName)
            ? worker.WorkerId
            : directoryUser.SamAccountName!;
        foreach (var mapping in enabledMappings)
        {
            var sourceValue = GetSourceValue(worker, mapping.Source, mapping.Target, proposedEmailAddress, _emailAddressPolicy);
            var proposedValue = ActiveDirectoryAttributeConstraints.NormalizeValue(
                mapping.Target,
                Transform(sourceValue, mapping.Transform));
            var currentValue = GetDirectoryValue(currentAttributes, mapping.Target);
            var before = string.IsNullOrWhiteSpace(currentValue) ? UnsetValue : currentValue!;
            var after = string.IsNullOrWhiteSpace(proposedValue) ? UnsetValue : proposedValue!;
            var changed = !string.Equals(before, after, StringComparison.Ordinal);

            _logger.LogDebug(
                "Evaluated attribute mapping. Changed={Changed}",
                changed);

            if (!string.IsNullOrWhiteSpace(logPath))
            {
                await _logWriter.AppendAsync(logPath, new WorkerPreviewLogEntry(
                    Event: "preview.diff.mapping",
                    WorkerId: worker.WorkerId,
                    Timestamp: DateTimeOffset.UtcNow,
                    Target: mapping.Target,
                    Source: mapping.Source,
                    SourceValue: sourceValue,
                    CurrentValue: currentValue,
                    ProposedValue: proposedValue,
                    Changed: changed,
                    Message: changed ? "Changed" : "Unchanged or filtered"), cancellationToken);
            }

            changes.Add(new AttributeChange(
                Attribute: mapping.Target,
                Source: mapping.Source,
                Before: before,
                After: after,
                Changed: changed));
        }

        var proposedDisplayName = ActiveDirectoryAttributeConstraints.NormalizeValue(
            DisplayNameAttribute,
            DirectoryIdentityFormatter.BuildDisplayName(worker.PreferredName, worker.LastName))!;
        var normalizedSamAccountName = ActiveDirectoryAttributeConstraints.NormalizeValue(SamAccountNameAttribute, proposedSamAccountName)!;
        var normalizedCommonName = ActiveDirectoryAttributeConstraints.NormalizeValue("cn", proposedSamAccountName)!;
        UpsertSystemAttributeChange(
            changes,
            attribute: SamAccountNameAttribute,
            source: "workerId",
            before: FormatValue(directoryUser?.SamAccountName ?? GetDirectoryValue(currentAttributes, SamAccountNameAttribute)),
            after: FormatValue(normalizedSamAccountName),
            changed: !string.Equals(directoryUser?.SamAccountName ?? GetDirectoryValue(currentAttributes, SamAccountNameAttribute), normalizedSamAccountName, StringComparison.OrdinalIgnoreCase));
        UpsertSystemAttributeChange(
            changes,
            attribute: "cn",
            source: SamAccountNameAttribute,
            before: FormatValue(GetDirectoryValue(currentAttributes, "cn")),
            after: FormatValue(normalizedCommonName),
            changed: !string.Equals(GetDirectoryValue(currentAttributes, "cn"), normalizedCommonName, StringComparison.Ordinal));
        UpsertSystemAttributeChange(
            changes,
            attribute: DisplayNameAttribute,
            source: "preferredName,lastName",
            before: FormatValue(GetDirectoryValue(currentAttributes, DisplayNameAttribute)),
            after: proposedDisplayName,
            changed: !string.Equals(GetDirectoryValue(currentAttributes, DisplayNameAttribute), proposedDisplayName, StringComparison.Ordinal));
        var isCreate = string.IsNullOrWhiteSpace(directoryUser?.SamAccountName);
        var isNameChange = HasNameChange(changes);
        var currentUserPrincipalName = GetDirectoryValue(currentAttributes, UserPrincipalNameAttribute);
        var currentMail = GetDirectoryValue(currentAttributes, "mail");
        var proposedMail = ResolveProposedMailValue(isCreate, isNameChange, proposedEmailAddress, currentMail);
        UpsertSystemAttributeChange(
            changes,
            attribute: UserPrincipalNameAttribute,
            source: "resolved email local-part",
            before: FormatValue(currentUserPrincipalName),
            after: FormatValue(isCreate
                ? ActiveDirectoryAttributeConstraints.NormalizeValue(UserPrincipalNameAttribute, proposedEmailAddress)
                : currentUserPrincipalName),
            changed: isCreate && !string.Equals(
                currentUserPrincipalName,
                ActiveDirectoryAttributeConstraints.NormalizeValue(UserPrincipalNameAttribute, proposedEmailAddress),
                StringComparison.Ordinal));
        UpsertSystemAttributeChange(
            changes,
            attribute: "mail",
            source: "resolved email local-part",
            before: FormatValue(currentMail),
            after: FormatValue(proposedMail),
            changed: !string.Equals(currentMail, proposedMail, StringComparison.Ordinal));
        UpsertSystemAttributeChange(
            changes,
            attribute: ProxyAddressesAttribute,
            source: "resolved email local-part",
            before: FormatValue(GetDirectoryValue(currentAttributes, ProxyAddressesAttribute)),
            after: FormatValue(BuildProxyAddressesValue(currentAttributes, proposedMail, isCreate || isNameChange)),
            changed: !string.Equals(
                GetDirectoryValue(currentAttributes, ProxyAddressesAttribute),
                BuildProxyAddressesValue(currentAttributes, proposedMail, isCreate || isNameChange),
                StringComparison.Ordinal));

        _logger.LogDebug(
            "Attribute diff completed. EnabledMappings={EnabledMappings} ChangedMappings={ChangedMappings}",
            enabledMappings.Count,
            changes.Count(change => change.Changed));

        if (!string.IsNullOrWhiteSpace(logPath))
        {
            await _logWriter.AppendAsync(logPath, new WorkerPreviewLogEntry(
                Event: "preview.diff.complete",
                WorkerId: worker.WorkerId,
                Timestamp: DateTimeOffset.UtcNow,
                Target: null,
                Source: null,
                SourceValue: null,
                CurrentValue: null,
                ProposedValue: null,
                Changed: null,
                Message: $"Completed with {changes.Count} changed mappings."), cancellationToken);
        }

        return changes;
    }

    private static string? GetSourceValue(
        WorkerSnapshot worker,
        string source,
        string target,
        string? proposedEmailAddress,
        IEmailAddressPolicy emailAddressPolicy)
    {
        return SourceValueResolver.ResolveSourceValue(worker, source, target, proposedEmailAddress, emailAddressPolicy);
    }

    internal static bool TryParseConcatSource(string? source, out IReadOnlyList<string> keys)
    {
        const string prefix = "Concat(";
        if (string.IsNullOrWhiteSpace(source) ||
            !source.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ||
            !source.EndsWith(')'))
        {
            keys = [];
            return false;
        }

        keys = source[prefix.Length..^1]
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(key => !string.IsNullOrWhiteSpace(key))
            .ToArray();

        return keys.Count > 0;
    }

    private static string? GetDirectoryValue(IReadOnlyDictionary<string, string?> attributes, string target)
    {
        return attributes.TryGetValue(target, out var value) ? value : null;
    }

    private static string? Transform(string? value, string transform)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return value;
        }

        return transform switch
        {
            "Trim" => value.Trim(),
            "Lower" => value.Trim().ToLowerInvariant(),
            "TrimStripCommasPeriods" => StripCharacters(value.Trim(), ',', '.'),
            "DateOnly" => SourceDateParser.TryParse(value, out var parsed)
                ? parsed.ToString("yyyy-MM-dd")
                : value,
            _ => value
        };
    }

    private static string StripCharacters(string value, params char[] characters)
    {
        return new string(value.Where(character => !characters.Contains(character)).ToArray());
    }

    private static void UpsertSystemAttributeChange(
        IList<AttributeChange> changes,
        string attribute,
        string source,
        string before,
        string after,
        bool changed)
    {
        var replacement = new AttributeChange(attribute, source, before, after, changed);
        var existingIndex = -1;
        for (var index = 0; index < changes.Count; index++)
        {
            if (string.Equals(changes[index].Attribute, attribute, StringComparison.OrdinalIgnoreCase))
            {
                existingIndex = index;
                break;
            }
        }

        if (existingIndex >= 0)
        {
            changes[existingIndex] = replacement;
            return;
        }

        changes.Insert(0, replacement);
    }

    private static string FormatValue(string? value) => string.IsNullOrWhiteSpace(value) ? UnsetValue : value!;

    private static bool HasNameChange(IEnumerable<AttributeChange> changes)
    {
        return changes.Any(change =>
            change.Changed &&
            (string.Equals(change.Attribute, "GivenName", StringComparison.OrdinalIgnoreCase) ||
             string.Equals(change.Attribute, "givenName", StringComparison.OrdinalIgnoreCase) ||
             string.Equals(change.Attribute, "Surname", StringComparison.OrdinalIgnoreCase) ||
             string.Equals(change.Attribute, "sn", StringComparison.OrdinalIgnoreCase) ||
             string.Equals(change.Attribute, DisplayNameAttribute, StringComparison.OrdinalIgnoreCase)));
    }

    private static string? ResolveProposedMailValue(
        bool isCreate,
        bool isNameChange,
        string? proposedEmailAddress,
        string? currentMail)
    {
        if (isCreate || isNameChange)
        {
            return ActiveDirectoryAttributeConstraints.NormalizeValue("mail", proposedEmailAddress);
        }

        return currentMail;
    }

    private static string? BuildProxyAddressesValue(
        IReadOnlyDictionary<string, string?> currentAttributes,
        string? proposedMail,
        bool shouldSetPrimary)
    {
        if (!shouldSetPrimary || string.IsNullOrWhiteSpace(proposedMail))
        {
            return GetDirectoryValue(currentAttributes, ProxyAddressesAttribute);
        }

        var normalizedPrimary = ActiveDirectoryAttributeConstraints.NormalizeValue("mail", proposedMail);
        if (string.IsNullOrWhiteSpace(normalizedPrimary))
        {
            return GetDirectoryValue(currentAttributes, ProxyAddressesAttribute);
        }

        var currentProxyAddresses = ParseProxyAddresses(GetDirectoryValue(currentAttributes, ProxyAddressesAttribute));
        var currentMail = ActiveDirectoryAttributeConstraints.NormalizeValue("mail", GetDirectoryValue(currentAttributes, "mail"));
        var values = new List<string> { $"SMTP:{normalizedPrimary}" };
        var seenAddresses = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { normalizedPrimary };

        foreach (var address in currentProxyAddresses)
        {
            var normalizedAddress = NormalizeProxyAddress(address);
            if (normalizedAddress is null || !seenAddresses.Add(normalizedAddress.Value.Address))
            {
                continue;
            }

            values.Add(normalizedAddress.Value.IsSmtp
                ? $"smtp:{normalizedAddress.Value.Address}"
                : address);
        }

        if (!string.IsNullOrWhiteSpace(currentMail) && seenAddresses.Add(currentMail))
        {
            values.Add($"smtp:{currentMail}");
        }

        return string.Join('\n', values);
    }

    private static IReadOnlyList<string> ParseProxyAddresses(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || string.Equals(value, UnsetValue, StringComparison.Ordinal))
        {
            return [];
        }

        return value
            .Split(['\n', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(address => !string.IsNullOrWhiteSpace(address))
            .ToArray();
    }

    private static (bool IsSmtp, string Address)? NormalizeProxyAddress(string value)
    {
        if (value.StartsWith("SMTP:", StringComparison.OrdinalIgnoreCase))
        {
            var address = ActiveDirectoryAttributeConstraints.NormalizeValue("mail", value[5..]);
            return string.IsNullOrWhiteSpace(address) ? null : (true, address);
        }

        return (false, value);
    }
}

namespace SyncFactors.Domain;

public sealed class AmbiguousDirectoryIdentityException : InvalidOperationException
{
    public AmbiguousDirectoryIdentityException(
        string lookupKind,
        string lookupValue,
        string identityAttribute,
        IReadOnlyList<string> distinguishedNames)
        : base(BuildMessage(lookupKind, lookupValue, identityAttribute, distinguishedNames))
    {
        LookupKind = lookupKind;
        LookupValue = lookupValue;
        IdentityAttribute = identityAttribute;
        DistinguishedNames = distinguishedNames;
    }

    public string LookupKind { get; }

    public string LookupValue { get; }

    public string IdentityAttribute { get; }

    public IReadOnlyList<string> DistinguishedNames { get; }

    private static string BuildMessage(
        string lookupKind,
        string lookupValue,
        string identityAttribute,
        IReadOnlyList<string> distinguishedNames)
    {
        var resolvedKind = string.IsNullOrWhiteSpace(lookupKind) ? "directory identity" : lookupKind.Trim();
        var resolvedIdentityAttribute = string.IsNullOrWhiteSpace(identityAttribute) ? "identity attribute" : identityAttribute.Trim();
        var resolvedLookupValue = string.IsNullOrWhiteSpace(lookupValue) ? "(blank)" : lookupValue.Trim();
        var matchedEntries = distinguishedNames.Count == 0
            ? "(none)"
            : string.Join(", ", distinguishedNames);

        return $"Ambiguous AD {resolvedKind} lookup for '{resolvedLookupValue}' via {resolvedIdentityAttribute}. Matched entries: {matchedEntries}.";
    }
}

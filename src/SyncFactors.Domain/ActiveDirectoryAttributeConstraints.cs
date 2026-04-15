using SyncFactors.Contracts;

namespace SyncFactors.Domain;

internal sealed record AttributeLengthViolation(
    string Attribute,
    string NormalizedAttribute,
    string? Source,
    int Length,
    int MaxLength);

internal static class ActiveDirectoryAttributeConstraints
{
    private static readonly IReadOnlyDictionary<string, string> AttributeAliases =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["GivenName"] = "givenName",
            ["Surname"] = "sn",
            ["UserPrincipalName"] = "userPrincipalName",
            ["Office"] = "physicalDeliveryOfficeName"
        };

    private static readonly IReadOnlyDictionary<string, int> MaxLengths =
        new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
        {
            ["cn"] = 64,
            ["company"] = 64,
            ["department"] = 64,
            ["displayName"] = 256,
            ["division"] = 256,
            ["employeeID"] = 16,
            ["employeeType"] = 256,
            ["extensionAttribute1"] = 1024,
            ["extensionAttribute2"] = 1024,
            ["extensionAttribute3"] = 1024,
            ["extensionAttribute4"] = 1024,
            ["extensionAttribute5"] = 1024,
            ["extensionAttribute6"] = 1024,
            ["extensionAttribute7"] = 1024,
            ["extensionAttribute8"] = 1024,
            ["extensionAttribute9"] = 1024,
            ["extensionAttribute10"] = 1024,
            ["extensionAttribute11"] = 1024,
            ["extensionAttribute12"] = 1024,
            ["extensionAttribute13"] = 1024,
            ["extensionAttribute14"] = 1024,
            ["extensionAttribute15"] = 1024,
            ["givenName"] = 64,
            ["l"] = 128,
            ["mail"] = 256,
            ["physicalDeliveryOfficeName"] = 128,
            ["postalCode"] = 40,
            ["sAMAccountName"] = 256,
            ["sn"] = 64,
            ["streetAddress"] = 1024,
            ["title"] = 128,
            ["userPrincipalName"] = 1024
        };

    public static IReadOnlyList<AttributeLengthViolation> GetViolations(IReadOnlyList<AttributeChange> changes)
    {
        var violations = new List<AttributeLengthViolation>();
        foreach (var change in changes.Where(change => change.Changed))
        {
            AddViolationIfNeeded(violations, change.Attribute, change.After, change.Source);
        }

        return violations;
    }

    public static IReadOnlyList<AttributeLengthViolation> GetViolations(
        DirectoryMutationCommand command,
        string? identityAttribute = null)
    {
        var violations = new List<AttributeLengthViolation>();
        var validatedAttributes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        AddViolationIfNeeded(violations, "sAMAccountName", command.SamAccountName, source: "workerId");
        validatedAttributes.Add(Normalize("sAMAccountName"));
        AddViolationIfNeeded(violations, "cn", command.CommonName, source: "sAMAccountName");
        validatedAttributes.Add(Normalize("cn"));
        AddViolationIfNeeded(violations, "displayName", command.DisplayName, source: "preferredName,lastName");
        validatedAttributes.Add(Normalize("displayName"));
        AddViolationIfNeeded(violations, "userPrincipalName", command.UserPrincipalName, source: "resolved email local-part");
        validatedAttributes.Add(Normalize("userPrincipalName"));
        AddViolationIfNeeded(violations, "mail", command.Mail, source: "resolved email local-part");
        validatedAttributes.Add(Normalize("mail"));

        foreach (var attribute in command.Attributes)
        {
            if (validatedAttributes.Contains(Normalize(attribute.Key)))
            {
                continue;
            }

            AddViolationIfNeeded(violations, attribute.Key, attribute.Value, source: null);
        }

        if (!string.IsNullOrWhiteSpace(identityAttribute) &&
            command.Attributes.All(attribute => !string.Equals(Normalize(attribute.Key), Normalize(identityAttribute), StringComparison.OrdinalIgnoreCase)))
        {
            AddViolationIfNeeded(violations, identityAttribute, command.WorkerId, source: "workerId");
        }

        return violations;
    }

    public static string BuildReason(IReadOnlyList<AttributeLengthViolation> violations)
    {
        if (violations.Count == 0)
        {
            return "Mapped AD attribute value exceeds the schema length limit.";
        }

        return violations.Count == 1
            ? $"Mapped AD attribute value exceeds the schema length limit: {DescribeViolation(violations[0])}."
            : $"Mapped AD attribute values exceed schema length limits: {string.Join("; ", violations.Take(3).Select(DescribeViolation))}{(violations.Count > 3 ? "; additional attributes omitted" : string.Empty)}.";
    }

    public static string BuildValidationMessage(IReadOnlyList<AttributeLengthViolation> violations)
    {
        if (violations.Count == 0)
        {
            return "Active Directory attribute value exceeds the schema length limit.";
        }

        return violations.Count == 1
            ? $"Active Directory attribute value exceeds the schema length limit: {DescribeViolation(violations[0])}."
            : $"Active Directory attribute values exceed schema length limits: {string.Join("; ", violations.Take(3).Select(DescribeViolation))}{(violations.Count > 3 ? "; additional attributes omitted" : string.Empty)}.";
    }

    private static void AddViolationIfNeeded(
        ICollection<AttributeLengthViolation> violations,
        string attribute,
        string? value,
        string? source)
    {
        if (string.IsNullOrWhiteSpace(attribute) ||
            string.IsNullOrWhiteSpace(value) ||
            string.Equals(value, "(unset)", StringComparison.Ordinal))
        {
            return;
        }

        var normalizedAttribute = Normalize(attribute);
        if (!MaxLengths.TryGetValue(normalizedAttribute, out var maxLength))
        {
            return;
        }

        if (value.Length <= maxLength)
        {
            return;
        }

        violations.Add(new AttributeLengthViolation(
            Attribute: attribute,
            NormalizedAttribute: normalizedAttribute,
            Source: source,
            Length: value.Length,
            MaxLength: maxLength));
    }

    private static string Normalize(string attribute)
    {
        return AttributeAliases.TryGetValue(attribute, out var normalized)
            ? normalized
            : attribute;
    }

    private static string DescribeViolation(AttributeLengthViolation violation)
    {
        return string.IsNullOrWhiteSpace(violation.Source)
            ? $"{violation.Attribute} length {violation.Length} exceeds max {violation.MaxLength}"
            : $"{violation.Attribute} from {violation.Source} length {violation.Length} exceeds max {violation.MaxLength}";
    }
}

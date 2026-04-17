using SyncFactors.Contracts;

namespace SyncFactors.Domain;

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

    public static string? NormalizeValue(string attribute, string? value)
    {
        if (string.IsNullOrWhiteSpace(attribute) ||
            string.IsNullOrWhiteSpace(value) ||
            string.Equals(value, "(unset)", StringComparison.Ordinal))
        {
            return value;
        }

        var normalizedAttribute = Normalize(attribute);
        var normalizedValue = normalizedAttribute is "userPrincipalName" or "mail"
            ? value.Trim().ToLowerInvariant()
            : value;
        if (!MaxLengths.TryGetValue(normalizedAttribute, out var maxLength) ||
            normalizedValue.Length <= maxLength)
        {
            return normalizedValue;
        }

        return normalizedValue[..maxLength];
    }

    private static string Normalize(string attribute)
    {
        return AttributeAliases.TryGetValue(attribute, out var normalized)
            ? normalized
            : attribute;
    }
}

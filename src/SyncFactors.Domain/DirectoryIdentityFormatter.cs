using System.Globalization;
using System.Text;

namespace SyncFactors.Domain;

public static class DirectoryIdentityFormatter
{
    public const string CorporateEmailDomain = "spireenergy.com";

    public static string BuildDisplayName(string firstName, string lastName)
    {
        return $"{lastName.Trim()}, {firstName.Trim()}";
    }

    public static string BuildBaseEmailLocalPart(string firstName, string lastName)
    {
        var normalizedFirstName = NormalizeNamePart(firstName);
        var normalizedLastName = NormalizeNamePart(lastName);

        return $"{normalizedFirstName}.{normalizedLastName}".Trim('.');
    }

    public static string BuildEmailAddress(string localPart)
    {
        return $"{localPart}@{CorporateEmailDomain}";
    }

    public static string NormalizeNamePart(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var normalized = value.Normalize(NormalizationForm.FormD);
        var builder = new StringBuilder(normalized.Length);

        foreach (var ch in normalized)
        {
            var category = CharUnicodeInfo.GetUnicodeCategory(ch);
            if (category == UnicodeCategory.NonSpacingMark)
            {
                continue;
            }

            if (char.IsLetterOrDigit(ch))
            {
                builder.Append(char.ToLowerInvariant(ch));
            }
        }

        return builder.ToString();
    }
}

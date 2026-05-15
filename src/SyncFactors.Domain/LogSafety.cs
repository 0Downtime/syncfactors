using System.Text;
using System.Text.RegularExpressions;

namespace SyncFactors.Domain;

public static partial class LogSafety
{
    private const int RegexTimeoutMilliseconds = 250;

    public static string SingleLine(string? value, int maxLength = 240, string emptyValue = "(empty)")
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return emptyValue;
        }

        var builder = new StringBuilder(value.Length);
        var previousWasWhitespace = false;

        foreach (var character in value)
        {
            if (character is '\r' or '\n' || char.IsControl(character))
            {
                if (!previousWasWhitespace && builder.Length > 0)
                {
                    builder.Append(' ');
                    previousWasWhitespace = true;
                }

                continue;
            }

            if (char.IsWhiteSpace(character))
            {
                if (!previousWasWhitespace && builder.Length > 0)
                {
                    builder.Append(' ');
                    previousWasWhitespace = true;
                }

                continue;
            }

            builder.Append(character);
            previousWasWhitespace = false;
        }

        var sanitized = builder.ToString().Trim();
        if (sanitized.Length == 0)
        {
            return emptyValue;
        }

        return sanitized.Length <= maxLength
            ? sanitized
            : sanitized[..maxLength];
    }

    public static string RedactPii(string? value, int maxLength = 2000, string emptyValue = "(empty)")
    {
        var sanitized = SingleLine(value, maxLength, emptyValue);
        return RedactPiiInText(sanitized);
    }

    public static string RedactPiiInText(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "(empty)";
        }

        var redacted = value;
        redacted = DistinguishedNamePattern().Replace(redacted, "[REDACTED:DistinguishedName]");
        redacted = EmailAddressPattern().Replace(redacted, "[REDACTED:Email]");
        redacted = DownLevelAccountPattern().Replace(redacted, "[REDACTED:Account]");
        redacted = SensitiveJsonPropertyPattern().Replace(
            redacted,
            match => $"{match.Groups["prefix"].Value}[REDACTED:{NormalizeKey(match.Groups["prefix"].Value)}]{match.Groups["suffix"].Value}");
        redacted = SensitiveKeyValuePattern().Replace(
            redacted,
            match => $"{match.Groups["key"].Value}=[REDACTED:{NormalizeKey(match.Groups["key"].Value)}]");
        redacted = WorkerPhrasePattern().Replace(
            redacted,
            match => $"{match.Groups["label"].Value} [REDACTED:{NormalizeKey(match.Groups["label"].Value)}]");
        return redacted;
    }

    public static object? RedactStructuredValue(object? value)
    {
        return value switch
        {
            null => null,
            string stringValue => RedactPii(stringValue),
            IEnumerable<KeyValuePair<string, object?>> pairs => pairs.ToDictionary(
                pair => pair.Key,
                pair => IsSensitiveKey(pair.Key)
                    ? $"[REDACTED:{NormalizeKey(pair.Key)}]"
                    : RedactStructuredValue(pair.Value),
                StringComparer.OrdinalIgnoreCase),
            _ => value
        };
    }

    private static bool IsSensitiveKey(string key)
    {
        return SensitiveKeyValuePattern().IsMatch($"{key}=x");
    }

    private static string NormalizeKey(string value)
    {
        var normalized = new string(value.Where(char.IsLetterOrDigit).ToArray());
        return normalized.Length == 0 ? "PII" : normalized;
    }

    [GeneratedRegex(@"(?<![A-Za-z0-9._%+\-'])[A-Za-z0-9._%+\-']+@[A-Za-z0-9.\-]+\.[A-Za-z]{2,}(?![A-Za-z0-9._%+\-'])", RegexOptions.CultureInvariant, RegexTimeoutMilliseconds)]
    private static partial Regex EmailAddressPattern();

    [GeneratedRegex(@"\b(?:CN|OU|DC)=[^;\r\n]+?(?:,(?:CN|OU|DC)=[^;\r\n]+?)+(?=\s+[A-Za-z][A-Za-z0-9]*=|[;\]\r\n]|$)", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase, RegexTimeoutMilliseconds)]
    private static partial Regex DistinguishedNamePattern();

    [GeneratedRegex(@"\b[A-Za-z0-9._-]+\\[A-Za-z0-9._$-]+\b", RegexOptions.CultureInvariant, RegexTimeoutMilliseconds)]
    private static partial Regex DownLevelAccountPattern();

    [GeneratedRegex(@"\b(?<key>BodyPreview|Clauses|CommonName|ConflictingValue|CurrentCn|CurrentOu|DesiredCn|DisplayName|DistinguishedName|DomainCnMatches|DomainMailMatches|DomainNameMatches|DomainSamAccountNameMatches|DomainUserPrincipalNameMatches|Email|EmailAddress|ExistingDisplayName|ExistingDistinguishedName|ExistingMail|ExistingSamAccountName|ExistingUserPrincipalName|Fields|GivenName|GroupDistinguishedName|IdentityLookupValue|IdentityValue|IdentityWriteValue|LookupClauses|Mail|ManagerDistinguishedName|ManagerId|Message|ParentOu|RequestedBy|SamAccountName|SearchBase|SearchBases|Surname|TargetOu|UserDistinguishedName|UserId|UserName|Username|UserPrincipalName|WorkerId)=(?<value>.*?)(?=\s+[A-Za-z][A-Za-z0-9]*=|[,;\]\r\n]|$)", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase, RegexTimeoutMilliseconds)]
    private static partial Regex SensitiveKeyValuePattern();

    [GeneratedRegex("(?<prefix>\"(?:displayName|email|emailAddress|firstName|givenName|lastName|managerId|personIdExternal|preferredName|samAccountName|sn|surname|userId|username|userPrincipalName|workerId)\"\\s*:\\s*\")(?<value>[^\"]+)(?<suffix>\")", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase, RegexTimeoutMilliseconds)]
    private static partial Regex SensitiveJsonPropertyPattern();

    [GeneratedRegex(@"\b(?<label>worker|manager|user)\s+(?<value>[0-9][A-Za-z0-9._@\\-]{2,}|[A-Za-z0-9._%+\-']+@[A-Za-z0-9.\-]+\.[A-Za-z]{2,})", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase, RegexTimeoutMilliseconds)]
    private static partial Regex WorkerPhrasePattern();
}

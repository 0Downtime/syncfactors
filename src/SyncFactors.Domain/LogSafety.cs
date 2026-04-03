using System.Text;

namespace SyncFactors.Domain;

public static class LogSafety
{
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
}

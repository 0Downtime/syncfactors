using System.Text.RegularExpressions;

namespace SyncFactors.Domain;

public static partial class SourceDateParser
{
    [GeneratedRegex(@"^/Date\((?<milliseconds>-?\d+)(?<offset>[+-]\d{4})?\)/$", RegexOptions.Compiled)]
    private static partial Regex ODataDateLiteralPattern();

    public static bool TryParse(string? value, out DateTimeOffset parsed)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            parsed = default;
            return false;
        }

        if (DateTimeOffset.TryParse(value, out parsed))
        {
            return true;
        }

        var match = ODataDateLiteralPattern().Match(value);
        if (match.Success &&
            long.TryParse(match.Groups["milliseconds"].Value, out var milliseconds))
        {
            parsed = DateTimeOffset.FromUnixTimeMilliseconds(milliseconds);
            return true;
        }

        parsed = default;
        return false;
    }
}

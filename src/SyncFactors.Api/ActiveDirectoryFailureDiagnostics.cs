using System.Text.RegularExpressions;

namespace SyncFactors.Api;

public sealed record FailureDiagnostics(
    string Summary,
    string? Guidance,
    IReadOnlyList<FailureDiagnosticItem> Details);

public sealed record FailureDiagnosticItem(
    string Label,
    string Value);

public static partial class ActiveDirectoryFailureDiagnostics
{
    private static readonly string[] PreferredDetailOrder =
    [
        "Step",
        "WorkerId",
        "SamAccountName",
        "DistinguishedName",
        "CurrentCn",
        "DesiredCn",
        "Attributes",
        "ManagerId"
    ];

    public static FailureDiagnostics? Parse(string? message)
    {
        if (string.IsNullOrWhiteSpace(message) ||
            !message.Contains("Active Directory", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var detailsTokenIndex = message.IndexOf(" Details: ", StringComparison.Ordinal);
        var guidanceTokenIndex = message.IndexOf(" Next check: ", StringComparison.Ordinal);
        var summary = detailsTokenIndex >= 0
            ? message[..detailsTokenIndex].Trim()
            : guidanceTokenIndex >= 0
                ? message[..guidanceTokenIndex].Trim()
                : message.Trim();
        var guidance = guidanceTokenIndex >= 0
            ? message[(guidanceTokenIndex + " Next check: ".Length)..].Trim()
            : null;

        if (detailsTokenIndex < 0)
        {
            return new FailureDiagnostics(summary, guidance, []);
        }

        var detailsStart = detailsTokenIndex + " Details: ".Length;
        var detailsLength = guidanceTokenIndex > detailsStart
            ? guidanceTokenIndex - detailsStart
            : message.Length - detailsStart;
        var detailsSegment = message.Substring(detailsStart, detailsLength).Trim();
        if (string.IsNullOrWhiteSpace(detailsSegment))
        {
            return new FailureDiagnostics(summary, guidance, []);
        }

        var details = ParseDetails(detailsSegment);
        return new FailureDiagnostics(summary, guidance, details);
    }

    private static IReadOnlyList<FailureDiagnosticItem> ParseDetails(string detailsSegment)
    {
        var matches = DetailKeyRegex().Matches(detailsSegment);
        if (matches.Count == 0)
        {
            return [new FailureDiagnosticItem("Details", detailsSegment)];
        }

        var items = new List<FailureDiagnosticItem>();
        for (var index = 0; index < matches.Count; index++)
        {
            var current = matches[index];
            var nextIndex = index + 1 < matches.Count ? matches[index + 1].Index : detailsSegment.Length;
            var key = current.Groups["key"].Value;
            var valueStart = current.Index + current.Length;
            var rawValue = detailsSegment[valueStart..nextIndex].Trim();
            if (string.IsNullOrWhiteSpace(rawValue))
            {
                continue;
            }

            items.Add(new FailureDiagnosticItem(FormatLabel(key), rawValue));
        }

        return items
            .OrderBy(item => Array.FindIndex(PreferredDetailOrder, key => string.Equals(FormatLabel(key), item.Label, StringComparison.Ordinal)))
            .ThenBy(item => item.Label, StringComparer.Ordinal)
            .ToArray();
    }

    private static string FormatLabel(string key)
    {
        return key switch
        {
            "WorkerId" => "Worker ID",
            "SamAccountName" => "SAM",
            "DistinguishedName" => "Distinguished Name",
            "CurrentCn" => "Current CN",
            "DesiredCn" => "Desired CN",
            "ManagerId" => "Manager ID",
            _ => key
        };
    }

    [GeneratedRegex(@"(?:(?<=^)|(?<=\s))(?<key>[A-Za-z]+)=", RegexOptions.CultureInvariant)]
    private static partial Regex DetailKeyRegex();
}

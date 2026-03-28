using System.Text.RegularExpressions;

namespace SyncFactors.MockSuccessFactors;

public sealed record ODataQuery(
    bool IsSupported,
    string? ErrorMessage,
    string IdentityField,
    string? WorkerId,
    IReadOnlySet<string> Select,
    IReadOnlySet<string> Expand);

public static partial class ODataQueryParser
{
    private static readonly string[] SupportedQueryKeys = ["$format", "$filter", "$select", "$expand"];

    public static ODataQuery Parse(IQueryCollection query)
    {
        foreach (var key in query.Keys)
        {
            if (!SupportedQueryKeys.Contains(key, StringComparer.OrdinalIgnoreCase))
            {
                return new ODataQuery(false, $"Unsupported query option '{key}'.", string.Empty, null, new HashSet<string>(StringComparer.OrdinalIgnoreCase), new HashSet<string>(StringComparer.OrdinalIgnoreCase));
            }
        }

        var format = query["$format"].ToString();
        if (!string.IsNullOrWhiteSpace(format) && !string.Equals(format, "json", StringComparison.OrdinalIgnoreCase))
        {
            return new ODataQuery(false, "Only $format=json is supported.", string.Empty, null, new HashSet<string>(StringComparer.OrdinalIgnoreCase), new HashSet<string>(StringComparer.OrdinalIgnoreCase));
        }

        var filter = query["$filter"].ToString();
        if (!TryParseIdentityFilter(filter, out var identityField, out var workerId))
        {
            return new ODataQuery(false, "Only filters in the form personIdExternal eq 'value' are supported.", string.Empty, null, new HashSet<string>(StringComparer.OrdinalIgnoreCase), new HashSet<string>(StringComparer.OrdinalIgnoreCase));
        }

        return new ODataQuery(
            IsSupported: true,
            ErrorMessage: null,
            IdentityField: identityField!,
            WorkerId: workerId,
            Select: ParseList(query["$select"].ToString()),
            Expand: ParseList(query["$expand"].ToString()));
    }

    private static HashSet<string> ParseList(string value)
    {
        return string.IsNullOrWhiteSpace(value)
            ? []
            : value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    private static bool TryParseIdentityFilter(string filter, out string? identityField, out string? workerId)
    {
        var match = IdentityFilterRegex().Match(filter ?? string.Empty);
        if (!match.Success)
        {
            identityField = null;
            workerId = null;
            return false;
        }

        identityField = match.Groups["field"].Value;
        workerId = match.Groups["value"].Value.Replace("''", "'", StringComparison.Ordinal);
        return true;
    }

    [GeneratedRegex(@"^\s*(?<field>[A-Za-z0-9_]+)\s+eq\s+'(?<value>(?:''|[^'])*)'\s*$", RegexOptions.Compiled)]
    private static partial Regex IdentityFilterRegex();
}

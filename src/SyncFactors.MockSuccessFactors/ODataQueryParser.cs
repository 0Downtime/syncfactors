using System.Text.RegularExpressions;

namespace SyncFactors.MockSuccessFactors;

public sealed record ODataQuery(
    bool IsSupported,
    string? ErrorMessage,
    string? Filter,
    string? OrderBy,
    string IdentityField,
    string? WorkerId,
    int? Top,
    int Skip,
    string? AsOfDate,
    IReadOnlySet<string> Select,
    IReadOnlySet<string> Expand);

public static partial class ODataQueryParser
{
    private static readonly string[] SupportedQueryKeys = ["$format", "$filter", "$orderby", "$select", "$expand", "$top", "$skip", "$skiptoken", "customPageSize", "paging", "asOfDate"];

    public static ODataQuery Parse(IQueryCollection query)
    {
        foreach (var key in query.Keys)
        {
            if (!SupportedQueryKeys.Contains(key, StringComparer.OrdinalIgnoreCase))
            {
                return new ODataQuery(false, $"Unsupported query option '{key}'.", null, null, string.Empty, null, null, 0, null, new HashSet<string>(StringComparer.OrdinalIgnoreCase), new HashSet<string>(StringComparer.OrdinalIgnoreCase));
            }
        }

        var format = GetQueryValue(query, "$format");
        if (!string.IsNullOrWhiteSpace(format) && !string.Equals(format, "json", StringComparison.OrdinalIgnoreCase))
        {
            return new ODataQuery(false, "Only $format=json is supported.", null, null, string.Empty, null, null, 0, null, new HashSet<string>(StringComparer.OrdinalIgnoreCase), new HashSet<string>(StringComparer.OrdinalIgnoreCase));
        }

        var top = GetQueryValue(query, "$top");
        if (!string.IsNullOrWhiteSpace(top) && (!int.TryParse(top, out var topValue) || topValue < 1))
        {
            return new ODataQuery(false, "Only positive integer values for $top are supported.", null, null, string.Empty, null, null, 0, null, new HashSet<string>(StringComparer.OrdinalIgnoreCase), new HashSet<string>(StringComparer.OrdinalIgnoreCase));
        }

        var customPageSize = GetQueryValue(query, "customPageSize");
        if (!string.IsNullOrWhiteSpace(customPageSize) && (!int.TryParse(customPageSize, out var customPageSizeValue) || customPageSizeValue < 1))
        {
            return new ODataQuery(false, "Only positive integer values for customPageSize are supported.", null, null, string.Empty, null, null, 0, null, new HashSet<string>(StringComparer.OrdinalIgnoreCase), new HashSet<string>(StringComparer.OrdinalIgnoreCase));
        }

        var skip = GetQueryValue(query, "$skip");
        if (!string.IsNullOrWhiteSpace(skip) && (!int.TryParse(skip, out var skipValue) || skipValue < 0))
        {
            return new ODataQuery(false, "Only non-negative integer values for $skip are supported.", null, null, string.Empty, null, null, 0, null, new HashSet<string>(StringComparer.OrdinalIgnoreCase), new HashSet<string>(StringComparer.OrdinalIgnoreCase));
        }

        var skipToken = GetQueryValue(query, "$skiptoken");
        if (!string.IsNullOrWhiteSpace(skipToken) && (!int.TryParse(skipToken, out var skipTokenValue) || skipTokenValue < 0))
        {
            return new ODataQuery(false, "Only non-negative integer values for $skiptoken are supported.", null, null, string.Empty, null, null, 0, null, new HashSet<string>(StringComparer.OrdinalIgnoreCase), new HashSet<string>(StringComparer.OrdinalIgnoreCase));
        }

        int? resolvedTop = !string.IsNullOrWhiteSpace(customPageSize)
            ? int.Parse(customPageSize)
            : string.IsNullOrWhiteSpace(top) ? null : int.Parse(top);
        var resolvedSkip = !string.IsNullOrWhiteSpace(skipToken)
            ? int.Parse(skipToken)
            : string.IsNullOrWhiteSpace(skip) ? 0 : int.Parse(skip);

        var filter = GetQueryValue(query, "$filter");
        if (string.IsNullOrWhiteSpace(filter))
        {
            return new ODataQuery(
                IsSupported: true,
                ErrorMessage: null,
                Filter: null,
                OrderBy: GetQueryValue(query, "$orderby"),
                IdentityField: string.Empty,
                WorkerId: null,
                Top: resolvedTop,
                Skip: resolvedSkip,
                AsOfDate: GetQueryValue(query, "asOfDate"),
                Select: ParseList(GetQueryValue(query, "$select")),
                Expand: ParseList(GetQueryValue(query, "$expand")));
        }

        if (!TryParseIdentityFilter(filter, out var identityField, out var workerId))
        {
            return new ODataQuery(
                IsSupported: true,
                ErrorMessage: null,
                Filter: filter,
                OrderBy: GetQueryValue(query, "$orderby"),
                IdentityField: string.Empty,
                WorkerId: null,
                Top: resolvedTop,
                Skip: resolvedSkip,
                AsOfDate: GetQueryValue(query, "asOfDate"),
                Select: ParseList(GetQueryValue(query, "$select")),
                Expand: ParseList(GetQueryValue(query, "$expand")));
        }

        return new ODataQuery(
            IsSupported: true,
            ErrorMessage: null,
            Filter: filter,
            OrderBy: GetQueryValue(query, "$orderby"),
            IdentityField: identityField!,
            WorkerId: workerId,
            Top: resolvedTop,
            Skip: resolvedSkip,
            AsOfDate: GetQueryValue(query, "asOfDate"),
            Select: ParseList(GetQueryValue(query, "$select")),
            Expand: ParseList(GetQueryValue(query, "$expand")));
    }

    private static HashSet<string> ParseList(string value)
    {
        return string.IsNullOrWhiteSpace(value)
            ? []
            : value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    private static string GetQueryValue(IQueryCollection query, string key)
    {
        if (query.TryGetValue(key, out var direct))
        {
            return direct.ToString();
        }

        var alternateKey = key.StartsWith('$') ? key[1..] : $"${key}";
        return query.TryGetValue(alternateKey, out var alternate)
            ? alternate.ToString()
            : string.Empty;
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

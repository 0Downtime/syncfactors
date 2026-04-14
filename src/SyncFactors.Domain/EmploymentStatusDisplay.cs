namespace SyncFactors.Domain;

public static class EmploymentStatusDisplay
{
    private static readonly IReadOnlyDictionary<string, EmploymentStatusInfo> KnownStatuses =
        new Dictionary<string, EmploymentStatusInfo>(StringComparer.OrdinalIgnoreCase)
        {
            ["A"] = new("A", "Active", "good"),
            ["64300"] = new("64300", "Active", "good"),
            ["U"] = new("U", "Paid Leave", "warn"),
            ["64304"] = new("64304", "Paid Leave", "warn"),
            ["64303"] = new("64303", "Unpaid Leave", "warn"),
            ["R"] = new("R", "Retired", "dim"),
            ["64307"] = new("64307", "Retired", "dim"),
            ["T"] = new("T", "Terminated", "bad"),
            ["I"] = new("I", "Terminated", "bad"),
            ["64308"] = new("64308", "Terminated", "bad")
        };

    public static EmploymentStatusInfo? Describe(string? code)
    {
        if (string.IsNullOrWhiteSpace(code))
        {
            return null;
        }

        var normalized = code.Trim();
        if (KnownStatuses.TryGetValue(normalized, out var knownStatus))
        {
            return knownStatus;
        }

        var inferredTone = normalized.ToLowerInvariant() switch
        {
            var value when value.Contains("leave", StringComparison.Ordinal) => "warn",
            var value when value.Contains("retir", StringComparison.Ordinal) => "dim",
            var value when value.Contains("term", StringComparison.Ordinal) => "bad",
            var value when value.Contains("inactive", StringComparison.Ordinal) => "bad",
            var value when value.Contains("active", StringComparison.Ordinal) => "good",
            _ => "neutral"
        };

        var label = char.IsDigit(normalized[0])
            ? normalized
            : Humanize(normalized);

        return new EmploymentStatusInfo(normalized, label, inferredTone);
    }

    public static string? Format(string? code)
    {
        var status = Describe(code);
        if (status is null)
        {
            return null;
        }

        return string.Equals(status.Code, status.Label, StringComparison.OrdinalIgnoreCase)
            ? status.Code
            : $"{status.Code} - {status.Label}";
    }

    private static string Humanize(string value)
    {
        var lower = value.Trim().ToLowerInvariant().Replace('_', ' ').Replace('-', ' ');
        return string.Join(
            ' ',
            lower.Split(' ', StringSplitOptions.RemoveEmptyEntries)
                .Select(segment => char.ToUpperInvariant(segment[0]) + segment[1..]));
    }
}

public sealed record EmploymentStatusInfo(
    string Code,
    string Label,
    string ToneCssClass)
{
    public string Display => string.Equals(Code, Label, StringComparison.OrdinalIgnoreCase)
        ? Code
        : $"{Code} - {Label}";

    public string PillText => $"Employment: {Label}";

    public string? DetailText => string.Equals(Code, Label, StringComparison.OrdinalIgnoreCase)
        ? null
        : $"Code {Code}";
}

public sealed record EmploymentStatusTotal(
    string Code,
    int Count);

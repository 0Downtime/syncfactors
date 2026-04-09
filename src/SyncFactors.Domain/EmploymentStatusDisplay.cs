namespace SyncFactors.Domain;

public static class EmploymentStatusDisplay
{
    private static readonly IReadOnlyDictionary<string, string> KnownLabels =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["64300"] = "Active",
            ["64307"] = "Retired",
            ["64308"] = "Terminated",
            ["64304"] = "Paid Leave",
            ["64303"] = "Unpaid Leave"
        };

    public static string? Format(string? code)
    {
        if (string.IsNullOrWhiteSpace(code))
        {
            return null;
        }

        var normalized = code.Trim();
        return KnownLabels.TryGetValue(normalized, out var label)
            ? $"{normalized} - {label}"
            : normalized;
    }
}

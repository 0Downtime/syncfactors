namespace SyncFactors.MockSuccessFactors;

public static class MockLifecycleState
{
    public const string Active = "active";
    public const string Preboarding = "preboarding";
    public const string PaidLeave = "paid-leave";
    public const string UnpaidLeave = "unpaid-leave";
    public const string Retired = "retired";
    public const string Terminated = "terminated";

    public static string Normalize(string? lifecycleState)
    {
        return lifecycleState?.Trim().ToLowerInvariant() switch
        {
            Preboarding => Preboarding,
            "prehire" => Preboarding,
            PaidLeave => PaidLeave,
            UnpaidLeave => UnpaidLeave,
            Retired => Retired,
            Terminated => Terminated,
            _ => Active
        };
    }

    public static string Infer(string startDate, string? employmentStatus, string? endDate, IReadOnlyList<string>? scenarioTags = null)
    {
        if (ContainsTag(scenarioTags, Preboarding) ||
            ContainsTag(scenarioTags, "prehire") ||
            IsFutureDate(startDate))
        {
            return Preboarding;
        }

        return employmentStatus?.Trim().ToUpperInvariant() switch
        {
            "U" => PaidLeave,
            "64304" => PaidLeave,
            "64303" => UnpaidLeave,
            "R" => Retired,
            "64307" => Retired,
            "T" => Terminated,
            "I" => Terminated,
            "64308" => Terminated,
            _ when !string.IsNullOrWhiteSpace(endDate) && !ContainsTag(scenarioTags, "active") => Terminated,
            _ => Active
        };
    }

    public static bool IsFutureDate(string? value)
    {
        return DateTimeOffset.TryParse(value, out var parsed) && parsed > DateTimeOffset.UtcNow;
    }

    private static bool ContainsTag(IReadOnlyList<string>? scenarioTags, string tag)
    {
        return scenarioTags?.Any(candidate => string.Equals(candidate, tag, StringComparison.OrdinalIgnoreCase)) == true;
    }
}

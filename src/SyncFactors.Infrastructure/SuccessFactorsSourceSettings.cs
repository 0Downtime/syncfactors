namespace SyncFactors.Infrastructure;

public sealed record SuccessFactorsSourceSettings(bool AllowScaffoldFallback)
{
    public static SuccessFactorsSourceSettings FromRunProfile(string? runProfile) =>
        new(string.Equals(runProfile, "mock", StringComparison.OrdinalIgnoreCase));
}

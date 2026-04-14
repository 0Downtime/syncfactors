using SyncFactors.Domain;

namespace SyncFactors.Domain.Tests;

public sealed class EmploymentStatusDisplayTests
{
    [Theory]
    [InlineData("64300", "64300 - Active")]
    [InlineData("A", "A - Active")]
    [InlineData("64307", "64307 - Retired")]
    [InlineData("R", "R - Retired")]
    [InlineData("64308", "64308 - Terminated")]
    [InlineData("T", "T - Terminated")]
    [InlineData("64304", "64304 - Paid Leave")]
    [InlineData("U", "U - Paid Leave")]
    [InlineData("64303", "64303 - Unpaid Leave")]
    [InlineData("99999", "99999")]
    public void Format_ReturnsExpectedDisplayValue(string code, string expected)
    {
        Assert.Equal(expected, EmploymentStatusDisplay.Format(code));
    }

    [Theory]
    [InlineData("64300", "Active", "good", "Employment: Active", "Code 64300")]
    [InlineData("64304", "Paid Leave", "warn", "Employment: Paid Leave", "Code 64304")]
    [InlineData("64307", "Retired", "dim", "Employment: Retired", "Code 64307")]
    [InlineData("64308", "Terminated", "bad", "Employment: Terminated", "Code 64308")]
    [InlineData("custom_status", "Custom Status", "neutral", "Employment: Custom Status", "Code custom_status")]
    public void Describe_ReturnsPillMetadata(string code, string label, string toneCssClass, string pillText, string? detailText)
    {
        var status = EmploymentStatusDisplay.Describe(code);

        Assert.NotNull(status);
        Assert.Equal(label, status!.Label);
        Assert.Equal(toneCssClass, status.ToneCssClass);
        Assert.Equal(pillText, status.PillText);
        Assert.Equal(detailText, status.DetailText);
    }
}

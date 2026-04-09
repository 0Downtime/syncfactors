using SyncFactors.Domain;

namespace SyncFactors.Domain.Tests;

public sealed class EmploymentStatusDisplayTests
{
    [Theory]
    [InlineData("64300", "64300 - Active")]
    [InlineData("64307", "64307 - Retired")]
    [InlineData("64308", "64308 - Terminated")]
    [InlineData("64304", "64304 - Paid Leave")]
    [InlineData("64303", "64303 - Unpaid Leave")]
    [InlineData("99999", "99999")]
    public void Format_ReturnsExpectedDisplayValue(string code, string expected)
    {
        Assert.Equal(expected, EmploymentStatusDisplay.Format(code));
    }
}

using SyncFactors.Infrastructure;

namespace SyncFactors.Infrastructure.Tests;

public sealed class ActiveDirectoryTransportModeFormatterTests
{
    [Theory]
    [InlineData("ldap", "ldap")]
    [InlineData("LDAP", "ldap")]
    [InlineData("ldaps", "ldaps")]
    [InlineData("LDAPS", "ldaps")]
    [InlineData("starttls", "starttls (ldap with StartTLS)")]
    [InlineData(" StartTls ", "starttls (ldap with StartTLS)")]
    [InlineData("custom", "custom")]
    [InlineData(" custom ", "custom")]
    [InlineData("", "ldaps")]
    [InlineData("   ", "ldaps")]
    public void DescribeStartupTransport_ReturnsExpectedLabel(string mode, string expected)
    {
        var actual = ActiveDirectoryTransportModeFormatter.DescribeStartupTransport(mode);

        Assert.Equal(expected, actual);
    }

    [Fact]
    public void DescribeStartupTransport_DefaultsToLdaps_WhenModeIsNull()
    {
        var actual = ActiveDirectoryTransportModeFormatter.DescribeStartupTransport(null);

        Assert.Equal("ldaps", actual);
    }
}

using System.Reflection;
using SyncFactors.Infrastructure;

namespace SyncFactors.Infrastructure.Tests;

public sealed class ActiveDirectoryGatewayTests
{
    [Fact]
    public void ResolveAvailableEmailLocalPart_ReturnsBaseCandidate_WhenAvailable()
    {
        var result = InvokeResolver("john.smith", static _ => false);

        Assert.Equal("john.smith", result);
    }

    [Fact]
    public void ResolveAvailableEmailLocalPart_AppendsTwo_WhenBaseCandidateIsTaken()
    {
        var result = InvokeResolver("john.smith", candidate => string.Equals(candidate, "john.smith", StringComparison.Ordinal));

        Assert.Equal("john.smith2", result);
    }

    [Fact]
    public void ResolveAvailableEmailLocalPart_AdvancesUntilCandidateIsAvailable()
    {
        var result = InvokeResolver(
            "john.smith",
            candidate => candidate is "john.smith" or "john.smith2" or "john.smith3");

        Assert.Equal("john.smith4", result);
    }

    private static string InvokeResolver(string baseLocalPart, Func<string, bool> candidateExists)
    {
        var method = typeof(ActiveDirectoryGateway).GetMethod(
            "ResolveAvailableEmailLocalPart",
            BindingFlags.NonPublic | BindingFlags.Static,
            [typeof(string), typeof(string), typeof(Func<string, bool>)]);
        Assert.NotNull(method);

        return Assert.IsType<string>(method!.Invoke(null, ["10001", baseLocalPart, candidateExists]));
    }
}

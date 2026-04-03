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

    [Fact]
    public void BuildEqualityFilter_EscapesAttributeNameAndValue()
    {
        var method = typeof(ActiveDirectoryGateway).GetMethod(
            "BuildEqualityFilter",
            BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);

        var filter = Assert.IsType<string>(method!.Invoke(null, ["employee)(Id", "user*)(test)\\name"]));

        Assert.Equal("(employee\\29\\28Id=user\\2a\\29\\28test\\29\\5cname)", filter);
    }

    [Fact]
    public void BuildAnyOfEqualityFilter_EscapesEachClause()
    {
        var method = typeof(ActiveDirectoryGateway).GetMethod(
            "BuildAnyOfEqualityFilter",
            BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);

        var clauses = new (string Attribute, string Value)[]
        {
            ("userPrincipalName", "user*)(test)"),
            ("mail", "user@example.com")
        };

        var filter = Assert.IsType<string>(method!.Invoke(null, [clauses]));

        Assert.Equal("(|(userPrincipalName=user\\2a\\29\\28test\\29)(mail=user@example.com))", filter);
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

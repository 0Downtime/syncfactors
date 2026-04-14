using System.Reflection;
using System.Linq;
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

        var filter = Assert.IsType<string>(method!.Invoke(null, ["employeeId", "user*)(test)\\name"]));

        Assert.Equal("(employeeId=user\\2a\\29\\28test\\29\\5cname)", filter);
    }

    [Fact]
    public void BuildEqualityFilter_RejectsInvalidAttributeName()
    {
        var method = typeof(ActiveDirectoryGateway).GetMethod(
            "BuildEqualityFilter",
            BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);

        var exception = Assert.Throws<TargetInvocationException>(() => method!.Invoke(null, ["employee)(Id", "worker-10001"]));

        Assert.IsType<InvalidOperationException>(exception.InnerException);
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

    [Fact]
    public void CreateSearchRequest_IncludesCommonNameAttribute()
    {
        var method = typeof(ActiveDirectoryGateway).GetMethod(
            "CreateSearchRequest",
            BindingFlags.NonPublic | BindingFlags.Static,
            [typeof(string), typeof(string), typeof(string), typeof(string)]);
        Assert.NotNull(method);

        var request = Assert.IsType<System.DirectoryServices.Protocols.SearchRequest>(
            method!.Invoke(null, ["OU=Users,DC=example,DC=com", "employeeID", "10001", "employeeID"]));

        Assert.Contains("cn", request.Attributes.Cast<string>());
    }

    [Fact]
    public void CreateSearchRequest_IncludesExtendedDirectoryAttributes()
    {
        var method = typeof(ActiveDirectoryGateway).GetMethod(
            "CreateSearchRequest",
            BindingFlags.NonPublic | BindingFlags.Static,
            [typeof(string), typeof(string), typeof(string), typeof(string)]);
        Assert.NotNull(method);

        var request = Assert.IsType<System.DirectoryServices.Protocols.SearchRequest>(
            method!.Invoke(null, ["OU=Users,DC=example,DC=com", "employeeID", "10001", "employeeID"]));

        var attributes = request.Attributes.Cast<string>().ToArray();

        Assert.Contains("manager", attributes);
        Assert.Contains("extensionAttribute5", attributes);
        Assert.Contains("extensionAttribute6", attributes);
        Assert.Contains("extensionAttribute7", attributes);
        Assert.Contains("extensionAttribute8", attributes);
        Assert.Contains("extensionAttribute9", attributes);
        Assert.Contains("extensionAttribute10", attributes);
        Assert.Contains("extensionAttribute11", attributes);
        Assert.Contains("extensionAttribute12", attributes);
        Assert.Contains("extensionAttribute13", attributes);
        Assert.Contains("extensionAttribute14", attributes);
        Assert.Contains("extensionAttribute15", attributes);
    }

    [Fact]
    public void GetEmailUniquenessSearchBases_UsesDefaultNamingContextWhenAvailable()
    {
        var method = typeof(ActiveDirectoryGateway).GetMethod(
            "GetEmailUniquenessSearchBases",
            BindingFlags.NonPublic | BindingFlags.Static,
            [typeof(string), typeof(ActiveDirectoryConfig)]);
        Assert.NotNull(method);

        var config = CreateConfig();

        var bases = Assert.IsAssignableFrom<IReadOnlyList<string>>(
            method!.Invoke(null, ["DC=example,DC=com", config]));

        var searchBase = Assert.Single(bases);
        Assert.Equal("DC=example,DC=com", searchBase);
    }

    [Fact]
    public void GetEmailUniquenessSearchBases_FallsBackToManagedOusWhenNamingContextMissing()
    {
        var method = typeof(ActiveDirectoryGateway).GetMethod(
            "GetEmailUniquenessSearchBases",
            BindingFlags.NonPublic | BindingFlags.Static,
            [typeof(string), typeof(ActiveDirectoryConfig)]);
        Assert.NotNull(method);

        var config = CreateConfig();

        var bases = Assert.IsAssignableFrom<IReadOnlyList<string>>(
            method!.Invoke(null, [null, config]));

        Assert.Equal(
            [
                "OU=Active,DC=example,DC=com",
                "OU=Prehire,DC=example,DC=com",
                "OU=Graveyard,DC=example,DC=com",
                "OU=Leave,DC=example,DC=com"
            ],
            bases);
    }

    [Fact]
    public void BuildLookupClauses_UsesMappedIdentityValueAndSamFallback()
    {
        var method = typeof(ActiveDirectoryGateway).GetMethod(
            "BuildLookupClauses",
            BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);

        var worker = new SyncFactors.Contracts.WorkerSnapshot(
            WorkerId: "user.10000",
            PreferredName: "Preferred10000",
            LastName: "Sample10000",
            Department: "IT",
            TargetOu: "OU=Users,DC=example,DC=com",
            IsPrehire: false,
            Attributes: new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
            {
                ["personIdExternal"] = "10000",
                ["userId"] = "user.10000"
            });
        var mappings = new[]
        {
            new SyncFactors.Domain.AttributeMapping("personIdExternal", "employeeID", Required: true, Transform: "Trim")
        };

        var clauses = Assert.IsAssignableFrom<IReadOnlyList<(string Attribute, string Value)>>(
            method!.Invoke(null, [worker, "employeeID", mappings]));

        Assert.Contains(("employeeID", "10000"), clauses);
        Assert.Contains(("sAMAccountName", "user.10000"), clauses);
    }

    [Fact]
    public void BuildLookupClauses_KeepsDistinctAttributesWhenIdentityMatchesWorkerId()
    {
        var method = typeof(ActiveDirectoryGateway).GetMethod(
            "BuildLookupClauses",
            BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);

        var worker = new SyncFactors.Contracts.WorkerSnapshot(
            WorkerId: "10000",
            PreferredName: "Preferred10000",
            LastName: "Sample10000",
            Department: "IT",
            TargetOu: "OU=Users,DC=example,DC=com",
            IsPrehire: false,
            Attributes: new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
            {
                ["employeeID"] = "10000"
            });

        var clauses = Assert.IsAssignableFrom<IReadOnlyList<(string Attribute, string Value)>>(
            method!.Invoke(null, [worker, "employeeID", Array.Empty<SyncFactors.Domain.AttributeMapping>()]));

        Assert.Equal(2, clauses.Count);
        Assert.Contains(("employeeID", "10000"), clauses);
        Assert.Contains(("sAMAccountName", "10000"), clauses);
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

    private static ActiveDirectoryConfig CreateConfig()
    {
        return new ActiveDirectoryConfig(
            Server: "localhost",
            Port: 389,
            Username: "bind",
            BindPassword: "secret",
            IdentityAttribute: "employeeID",
            DefaultActiveOu: "OU=Active,DC=example,DC=com",
            PrehireOu: "OU=Prehire,DC=example,DC=com",
            GraveyardOu: "OU=Graveyard,DC=example,DC=com",
            Transport: new ActiveDirectoryTransportConfig("ldap", false, false, false, []),
            IdentityPolicy: new ActiveDirectoryIdentityPolicyConfig(false),
            LeaveOu: "OU=Leave,DC=example,DC=com");
    }
}

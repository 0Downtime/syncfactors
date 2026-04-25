using System.Reflection;
using System.Linq;
using System.DirectoryServices.Protocols;
using SyncFactors.Infrastructure;
using SyncFactors.Domain;

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
    public void CreateOuListingRequest_UsesPagedResultsControl()
    {
        var method = typeof(ActiveDirectoryGateway).GetMethod(
            "CreateOuListingRequest",
            BindingFlags.NonPublic | BindingFlags.Static,
            [typeof(string), typeof(ActiveDirectoryConfig)]);
        Assert.NotNull(method);

        var request = Assert.IsType<System.DirectoryServices.Protocols.SearchRequest>(
            method!.Invoke(null, ["OU=Users,DC=example,DC=com", CreateConfig()]));

        var pageControl = Assert.IsType<System.DirectoryServices.Protocols.PageResultRequestControl>(
            Assert.Single(request.Controls.Cast<System.DirectoryServices.Protocols.DirectoryControl>()));
        Assert.Equal(500, pageControl.PageSize);
        Assert.Equal(System.DirectoryServices.Protocols.SearchScope.Subtree, request.Scope);
        Assert.Equal("(&(objectCategory=person)(objectClass=user))", request.Filter);
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

    [Fact]
    public void CreateAmbiguousDirectoryIdentityException_IncludesLookupContext()
    {
        var method = typeof(ActiveDirectoryGateway).GetMethod(
            "CreateAmbiguousDirectoryIdentityException",
            BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);

        var exception = Assert.IsType<AmbiguousDirectoryIdentityException>(method!.Invoke(
            null,
            [
                "manager identity",
                "10000",
                "employeeID",
                new[]
                {
                    "CN=10000,OU=Users,DC=example,DC=com",
                    "CN=user.10000,OU=Users,DC=example,DC=com"
                }
            ]));

        Assert.Contains("10000", exception.Message, StringComparison.Ordinal);
        Assert.Contains("employeeID", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ShouldRetryTransientLdapFailure_ReturnsTrue_ForUnavailableServerOnFirstAttempt()
    {
        var method = typeof(ActiveDirectoryGateway).GetMethod(
            "ShouldRetryTransientLdapFailure",
            BindingFlags.NonPublic | BindingFlags.Static,
            [typeof(LdapException), typeof(int)]);
        Assert.NotNull(method);

        var shouldRetry = Assert.IsType<bool>(method!.Invoke(null, [new LdapException("The LDAP server is unavailable."), 0]));

        Assert.True(shouldRetry);
    }

    [Fact]
    public void ShouldRetryTransientLdapFailure_ReturnsFalse_AfterRetryBudgetIsExhausted()
    {
        var method = typeof(ActiveDirectoryGateway).GetMethod(
            "ShouldRetryTransientLdapFailure",
            BindingFlags.NonPublic | BindingFlags.Static,
            [typeof(LdapException), typeof(int)]);
        Assert.NotNull(method);

        var shouldRetry = Assert.IsType<bool>(method!.Invoke(null, [new LdapException("The LDAP server is unavailable."), 3]));

        Assert.False(shouldRetry);
    }

    [Fact]
    public void ShouldRetryTransientLdapFailure_ReturnsFalse_ForNonTransientLdapFailures()
    {
        var method = typeof(ActiveDirectoryGateway).GetMethod(
            "ShouldRetryTransientLdapFailure",
            BindingFlags.NonPublic | BindingFlags.Static,
            [typeof(LdapException), typeof(int)]);
        Assert.NotNull(method);

        var shouldRetry = Assert.IsType<bool>(method!.Invoke(null, [new LdapException("Invalid credentials."), 0]));

        Assert.False(shouldRetry);
    }

    [Fact]
    public void ShouldRetryTransientLdapFailure_ReturnsTrue_ForMarkedTimeoutOnFirstAttempt()
    {
        var method = typeof(ActiveDirectoryGateway).GetMethod(
            "ShouldRetryTransientLdapFailure",
            BindingFlags.NonPublic | BindingFlags.Static,
            [typeof(InvalidOperationException), typeof(int)]);
        Assert.NotNull(method);

        var timeoutException = CreateTimeoutException();

        var shouldRetry = Assert.IsType<bool>(method!.Invoke(null, [timeoutException, 0]));

        Assert.True(shouldRetry);
    }

    [Fact]
    public void ShouldRetryTransientLdapFailure_ReturnsFalse_ForMarkedTimeoutAfterRetryBudgetIsExhausted()
    {
        var method = typeof(ActiveDirectoryGateway).GetMethod(
            "ShouldRetryTransientLdapFailure",
            BindingFlags.NonPublic | BindingFlags.Static,
            [typeof(InvalidOperationException), typeof(int)]);
        Assert.NotNull(method);

        var timeoutException = CreateTimeoutException();

        var shouldRetry = Assert.IsType<bool>(method!.Invoke(null, [timeoutException, 3]));

        Assert.False(shouldRetry);
    }

    [Fact]
    public void ShouldRetryTransientLdapFailure_ReturnsFalse_ForUnmarkedInvalidOperationException()
    {
        var method = typeof(ActiveDirectoryGateway).GetMethod(
            "ShouldRetryTransientLdapFailure",
            BindingFlags.NonPublic | BindingFlags.Static,
            [typeof(InvalidOperationException), typeof(int)]);
        Assert.NotNull(method);

        var shouldRetry = Assert.IsType<bool>(method!.Invoke(null, [new InvalidOperationException("AD lookup failed."), 0]));

        Assert.False(shouldRetry);
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

    private static InvalidOperationException CreateTimeoutException()
    {
        var method = typeof(SyncFactorsConfigurationLoader).Assembly
            .GetType("SyncFactors.Infrastructure.ExternalSystemExceptionFactory")
            ?.GetMethod(
                "CreateActiveDirectoryTimeoutException",
                BindingFlags.Public | BindingFlags.Static,
                [typeof(string), typeof(string), typeof(TimeSpan), typeof(Exception)]);
        Assert.NotNull(method);

        return Assert.IsType<InvalidOperationException>(
            method!.Invoke(null, ["lookup", "ldap.example.test", TimeSpan.FromSeconds(10), new TimeoutException("Timed out.")]));
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

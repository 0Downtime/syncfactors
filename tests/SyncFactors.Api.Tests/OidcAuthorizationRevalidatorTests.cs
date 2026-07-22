using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using SyncFactors.Infrastructure;

namespace SyncFactors.Api.Tests;

public sealed class OidcAuthorizationRevalidatorTests
{
    [Fact]
    public async Task RevalidateAsync_RevokesViewer_WhenProviderNoLongerReportsViewerGroup()
    {
        var client = new StubOidcUserInfoClient("subject-1", "unmapped-group");
        var revalidator = new OidcAuthorizationRevalidator(client);
        var principal = BuildOidcPrincipal("subject-1", SecurityRoles.Viewer);
        var options = CreateOptions();
        var properties = new AuthenticationProperties();
        properties.StoreTokens([new AuthenticationToken { Name = "access_token", Value = "access-token" }]);

        var result = await revalidator.RevalidateAsync(principal, properties, options, CancellationToken.None);

        Assert.False(result.IsAuthorized);
        Assert.Empty(result.Roles);
    }

    [Fact]
    public void ApplyRoles_DemotesAdminToOperator_WhenProviderGroupsChange()
    {
        var identity = new ClaimsIdentity(
        [
            new Claim(ClaimTypes.Role, SecurityRoles.Admin),
            new Claim(ClaimTypes.Role, SecurityRoles.Operator)
        ], "Cookies");

        OidcAuthorizationRevalidator.ApplyRoles(identity, [SecurityRoles.Operator]);

        Assert.DoesNotContain(identity.Claims, claim =>
            claim.Type == ClaimTypes.Role && claim.Value == SecurityRoles.Admin);
        Assert.Contains(identity.Claims, claim =>
            claim.Type == ClaimTypes.Role && claim.Value == SecurityRoles.Operator);
    }

    [Theory]
    [InlineData("viewer-group", SecurityRoles.Viewer)]
    [InlineData("operator-group", SecurityRoles.Operator)]
    [InlineData("admin-group", SecurityRoles.Admin)]
    public async Task RevalidateAsync_RetainsOnlyCurrentProviderRole(string providerGroup, string expectedRole)
    {
        var revalidator = new OidcAuthorizationRevalidator(new StubOidcUserInfoClient("subject-1", providerGroup));
        var properties = new AuthenticationProperties();
        properties.StoreTokens([new AuthenticationToken { Name = "access_token", Value = "access-token" }]);

        var result = await revalidator.RevalidateAsync(
            BuildOidcPrincipal("subject-1", SecurityRoles.Admin),
            properties,
            CreateOptions(),
            CancellationToken.None);

        Assert.True(result.IsAuthorized);
        Assert.Equal([expectedRole], result.Roles);
    }

    [Fact]
    public void ShouldRevalidate_PreservesLocalBreakGlassSessions()
    {
        var principal = new ClaimsPrincipal(new ClaimsIdentity(
        [
            new Claim(ClaimTypes.NameIdentifier, "break-glass-user"),
            new Claim(ClaimTypes.Role, SecurityRoles.BreakGlassAdmin),
            new Claim(SecurityClaimTypes.AuthSource, "local")
        ], "Cookies"));

        Assert.False(OidcAuthorizationRevalidator.ShouldRevalidate(principal));
    }

    [Theory]
    [InlineData("viewer-group", SecurityRoles.Viewer)]
    [InlineData("operator-group", SecurityRoles.Operator)]
    [InlineData("admin-group", SecurityRoles.Admin)]
    public async Task ValidateAsync_RefreshesOidcRole_FromCurrentProviderGroup(string group, string expectedRole)
    {
        var client = new StubOidcUserInfoClient("subject-1", group);
        var validator = new OidcCookiePrincipalValidator(
            new OidcAuthorizationRevalidator(client),
            CreateOptions());
        var principal = BuildOidcPrincipal("subject-1", SecurityRoles.Admin);
        var properties = CreateProperties();

        var isAuthorized = await validator.ValidateAsync(principal, properties, CancellationToken.None);

        Assert.True(isAuthorized);
        Assert.True(principal.IsInRole(expectedRole));
        Assert.False(principal.IsInRole(SecurityRoles.Admin) && expectedRole != SecurityRoles.Admin);
    }

    [Fact]
    public async Task ValidateAsync_RejectsOidcPrincipal_WhenProviderCannotRevalidate()
    {
        var validator = new OidcCookiePrincipalValidator(
            new OidcAuthorizationRevalidator(new ThrowingOidcUserInfoClient()),
            CreateOptions());

        var isAuthorized = await validator.ValidateAsync(
            BuildOidcPrincipal("subject-1", SecurityRoles.Operator),
            CreateProperties(),
            CancellationToken.None);

        Assert.False(isAuthorized);
    }

    [Fact]
    public async Task ValidateAsync_PreservesLocalBreakGlassPrincipal_WithoutCallingOidc()
    {
        var client = new CountingOidcUserInfoClient();
        var validator = new OidcCookiePrincipalValidator(
            new OidcAuthorizationRevalidator(client),
            CreateOptions());
        var principal = new ClaimsPrincipal(new ClaimsIdentity(
        [
            new Claim(ClaimTypes.NameIdentifier, "break-glass"),
            new Claim(ClaimTypes.Role, SecurityRoles.BreakGlassAdmin),
            new Claim(SecurityClaimTypes.AuthSource, "local")
        ], "Cookies"));

        var isAuthorized = await validator.ValidateAsync(principal, new AuthenticationProperties(), CancellationToken.None);

        Assert.True(isAuthorized);
        Assert.True(principal.IsInRole(SecurityRoles.BreakGlassAdmin));
        Assert.Equal(0, client.CallCount);
    }

    private static ClaimsPrincipal BuildOidcPrincipal(string subject, string role) =>
        new(new ClaimsIdentity(
        [
            new Claim(ClaimTypes.NameIdentifier, subject),
            new Claim(ClaimTypes.Role, role),
            new Claim(SecurityClaimTypes.AuthSource, "oidc")
        ], "Cookies"));

    private static LocalAuthOptions CreateOptions() => new()
    {
        Oidc = new OidcOptions
        {
            ViewerGroups = ["viewer-group"],
            OperatorGroups = ["operator-group"],
            AdminGroups = ["admin-group"]
        }
    };

    private static AuthenticationProperties CreateProperties()
    {
        var properties = new AuthenticationProperties();
        properties.StoreTokens([new AuthenticationToken { Name = "access_token", Value = "access-token" }]);
        return properties;
    }

    private sealed class StubOidcUserInfoClient(string subject, params string[] groups) : IOidcUserInfoClient
    {
        public Task<OidcUserInfo> GetUserInfoAsync(string accessToken, CancellationToken cancellationToken) =>
            Task.FromResult(new OidcUserInfo(subject, groups.Select(group => new Claim("groups", group)).ToArray()));
    }

    private sealed class ThrowingOidcUserInfoClient : IOidcUserInfoClient
    {
        public Task<OidcUserInfo> GetUserInfoAsync(string accessToken, CancellationToken cancellationToken) =>
            throw new HttpRequestException("Provider unavailable.");
    }

    private sealed class CountingOidcUserInfoClient : IOidcUserInfoClient
    {
        public int CallCount { get; private set; }

        public Task<OidcUserInfo> GetUserInfoAsync(string accessToken, CancellationToken cancellationToken)
        {
            CallCount++;
            return Task.FromResult(new OidcUserInfo("unexpected", []));
        }
    }
}

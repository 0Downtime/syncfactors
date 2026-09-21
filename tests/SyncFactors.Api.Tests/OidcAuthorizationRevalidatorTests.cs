using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using SyncFactors.Infrastructure;

namespace SyncFactors.Api.Tests;

public sealed class OidcAuthorizationRevalidatorTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-07-31T12:00:00Z");

    [Fact]
    public async Task ValidateAsync_AcceptsFreshValidatedRole_WithoutUserInfoOrAccessToken()
    {
        var validator = CreateValidator();
        var principal = BuildOidcPrincipal(
            "subject-1",
            SecurityRoles.Operator,
            Now.AddMinutes(-10));

        var isAuthorized = await validator.ValidateAsync(principal, CancellationToken.None);

        Assert.True(isAuthorized);
        Assert.True(principal.IsInRole(SecurityRoles.Operator));
    }

    [Fact]
    public async Task ValidateAsync_RejectsSnapshotAtRevalidationDeadline()
    {
        var validator = CreateValidator();
        var principal = BuildOidcPrincipal(
            "subject-1",
            SecurityRoles.Viewer,
            Now.AddMinutes(-60));

        var isAuthorized = await validator.ValidateAsync(principal, CancellationToken.None);

        Assert.False(isAuthorized);
    }

    [Fact]
    public async Task ValidateAsync_HonorsConfiguredRevalidationInterval()
    {
        var options = CreateOptions();
        options.Oidc.AuthorizationRevalidationMinutes = 15;
        var validator = CreateValidator(options);
        var principal = BuildOidcPrincipal(
            "subject-1",
            SecurityRoles.Admin,
            Now.AddMinutes(-15));

        var isAuthorized = await validator.ValidateAsync(principal, CancellationToken.None);

        Assert.False(isAuthorized);
    }

    [Theory]
    [InlineData(null, SecurityRoles.Viewer, "2026-07-31T11:50:00Z")]
    [InlineData("subject-1", "unrecognized-role", "2026-07-31T11:50:00Z")]
    [InlineData("subject-1", SecurityRoles.Viewer, null)]
    [InlineData("subject-1", SecurityRoles.Viewer, "not-a-timestamp")]
    [InlineData("subject-1", SecurityRoles.Viewer, "2026-07-31T12:00:01Z")]
    public async Task ValidateAsync_RejectsMalformedAuthorizationSnapshot(
        string? subject,
        string role,
        string? validatedAt)
    {
        var validator = CreateValidator();
        var principal = BuildOidcPrincipal(subject, role, validatedAt);

        var isAuthorized = await validator.ValidateAsync(principal, CancellationToken.None);

        Assert.False(isAuthorized);
    }

    [Fact]
    public async Task ValidateAsync_RejectsSnapshotWithMultipleEffectiveRoles()
    {
        var validator = CreateValidator();
        var principal = BuildOidcPrincipal(
            "subject-1",
            SecurityRoles.Viewer,
            Now.AddMinutes(-5));
        ((ClaimsIdentity)principal.Identity!).AddClaim(new Claim(ClaimTypes.Role, SecurityRoles.Admin));

        var isAuthorized = await validator.ValidateAsync(principal, CancellationToken.None);

        Assert.False(isAuthorized);
    }

    [Fact]
    public async Task ValidateAsync_PreservesLocalBreakGlassPrincipal()
    {
        var validator = CreateValidator();
        var principal = new ClaimsPrincipal(new ClaimsIdentity(
        [
            new Claim(ClaimTypes.NameIdentifier, "break-glass"),
            new Claim(ClaimTypes.Role, SecurityRoles.BreakGlassAdmin),
            new Claim(SecurityClaimTypes.AuthSource, "local")
        ], "Cookies"));

        var isAuthorized = await validator.ValidateAsync(principal, CancellationToken.None);

        Assert.True(isAuthorized);
        Assert.True(principal.IsInRole(SecurityRoles.BreakGlassAdmin));
    }

    [Fact]
    public void RetainIdToken_PrefersCodeFlowTokenEndpointToken_AndDropsAccessTokens()
    {
        var properties = new AuthenticationProperties();
        properties.StoreTokens(
        [
            new AuthenticationToken { Name = "access_token", Value = "access-token" },
            new AuthenticationToken { Name = "refresh_token", Value = "refresh-token" }
        ]);

        OidcLogoutTokenStore.RetainIdToken(
            properties,
            tokenEndpointIdToken: "token-endpoint-id-token",
            authorizationResponseIdToken: "authorization-response-id-token");

        var token = Assert.Single(properties.GetTokens());
        Assert.Equal("id_token", token.Name);
        Assert.Equal("token-endpoint-id-token", token.Value);
    }

    [Fact]
    public void RetainIdToken_UsesAuthorizationResponseFallback_WhenPresent()
    {
        var properties = new AuthenticationProperties();

        OidcLogoutTokenStore.RetainIdToken(
            properties,
            tokenEndpointIdToken: null,
            authorizationResponseIdToken: "authorization-response-id-token");

        Assert.Equal("authorization-response-id-token", properties.GetTokenValue("id_token"));
    }

    [Fact]
    public void ValidateAuthority_RejectsHttpAuthorityOutsideDevelopment()
    {
        var options = new OidcOptions { Authority = "http://login.example.test/tenant" };

        var exception = Assert.Throws<InvalidOperationException>(() =>
            OidcConfigurationValidator.ValidateAuthority(options, oidcEnabled: true, isDevelopment: false));

        Assert.Contains("absolute HTTPS URL", exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("https://login.example.test/tenant", false)]
    [InlineData("http://localhost:5000/tenant", true)]
    public void ValidateAuthority_AllowsSecureProductionOrDevelopmentAuthority(
        string authority,
        bool isDevelopment)
    {
        var options = new OidcOptions { Authority = authority };

        OidcConfigurationValidator.ValidateAuthority(options, oidcEnabled: true, isDevelopment);
    }

    private static OidcCookiePrincipalValidator CreateValidator(LocalAuthOptions? options = null) =>
        new(
            new OidcAuthorizationRevalidator(new FixedTimeProvider(Now)),
            options ?? CreateOptions());

    private static ClaimsPrincipal BuildOidcPrincipal(
        string? subject,
        string role,
        DateTimeOffset validatedAt) =>
        BuildOidcPrincipal(subject, role, validatedAt.ToString("O"));

    private static ClaimsPrincipal BuildOidcPrincipal(
        string? subject,
        string role,
        string? validatedAt)
    {
        var claims = new List<Claim>
        {
            new(ClaimTypes.Role, role),
            new(SecurityClaimTypes.AuthSource, "oidc")
        };
        if (subject is not null)
        {
            claims.Add(new Claim(ClaimTypes.NameIdentifier, subject));
        }

        if (validatedAt is not null)
        {
            claims.Add(new Claim(SecurityClaimTypes.OidcAuthorizationValidatedAt, validatedAt));
        }

        return new ClaimsPrincipal(new ClaimsIdentity(claims, "Cookies"));
    }

    private static LocalAuthOptions CreateOptions() => new()
    {
        Oidc = new OidcOptions
        {
            AuthorizationRevalidationMinutes = 60,
            ViewerGroups = ["viewer-group"],
            OperatorGroups = ["operator-group"],
            AdminGroups = ["admin-group"]
        }
    };

    private sealed class FixedTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => utcNow;
    }
}

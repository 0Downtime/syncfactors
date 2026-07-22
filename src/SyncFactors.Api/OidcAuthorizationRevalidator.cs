using System.Net.Http.Headers;
using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.Extensions.Options;
using SyncFactors.Infrastructure;

namespace SyncFactors.Api;

internal interface IOidcUserInfoClient
{
    Task<OidcUserInfo> GetUserInfoAsync(string accessToken, CancellationToken cancellationToken);
}

internal sealed record OidcUserInfo(string Subject, IReadOnlyList<Claim> Claims);

internal sealed record OidcAuthorizationRevalidationResult(bool IsAuthorized, IReadOnlyList<string> Roles);

internal sealed class OidcUserInfoClient(IOptionsMonitor<OpenIdConnectOptions> optionsMonitor) : IOidcUserInfoClient
{
    public async Task<OidcUserInfo> GetUserInfoAsync(string accessToken, CancellationToken cancellationToken)
    {
        var options = optionsMonitor.Get(OpenIdConnectDefaults.AuthenticationScheme);
        var configuration = await options.ConfigurationManager!.GetConfigurationAsync(cancellationToken);
        if (string.IsNullOrWhiteSpace(configuration.UserInfoEndpoint))
        {
            throw new InvalidOperationException("OIDC provider did not publish a UserInfo endpoint.");
        }

        using var request = new HttpRequestMessage(HttpMethod.Get, configuration.UserInfoEndpoint);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        using var response = await options.Backchannel.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();

        await using var responseStream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var document = await JsonDocument.ParseAsync(responseStream, cancellationToken: cancellationToken);
        var root = document.RootElement;
        if (!root.TryGetProperty("sub", out var subjectProperty) || subjectProperty.ValueKind != JsonValueKind.String)
        {
            throw new InvalidOperationException("OIDC UserInfo response did not contain a subject.");
        }

        return new OidcUserInfo(subjectProperty.GetString()!, ReadClaims(root));
    }

    private static IReadOnlyList<Claim> ReadClaims(JsonElement root)
    {
        var claims = new List<Claim>();
        foreach (var property in root.EnumerateObject())
        {
            if (property.Value.ValueKind == JsonValueKind.String)
            {
                claims.Add(new Claim(property.Name, property.Value.GetString()!));
            }
            else if (property.Value.ValueKind == JsonValueKind.Array)
            {
                foreach (var value in property.Value.EnumerateArray())
                {
                    if (value.ValueKind == JsonValueKind.String)
                    {
                        claims.Add(new Claim(property.Name, value.GetString()!));
                    }
                }
            }
        }

        return claims;
    }
}

internal sealed class OidcAuthorizationRevalidator(IOidcUserInfoClient userInfoClient)
{
    public async Task<OidcAuthorizationRevalidationResult> RevalidateAsync(
        ClaimsPrincipal principal,
        AuthenticationProperties properties,
        LocalAuthOptions authSettings,
        CancellationToken cancellationToken)
    {
        var subject = principal.FindFirstValue(ClaimTypes.NameIdentifier);
        var accessToken = properties.GetTokenValue("access_token");
        if (string.IsNullOrWhiteSpace(subject) || string.IsNullOrWhiteSpace(accessToken))
        {
            return new OidcAuthorizationRevalidationResult(false, []);
        }

        try
        {
            var userInfo = await userInfoClient.GetUserInfoAsync(accessToken, cancellationToken);
            if (!string.Equals(subject, userInfo.Subject, StringComparison.Ordinal))
            {
                return new OidcAuthorizationRevalidationResult(false, []);
            }

            var roles = OidcRoleResolver.ResolveRoles(new ClaimsIdentity(userInfo.Claims), authSettings);
            return new OidcAuthorizationRevalidationResult(roles.Count > 0, roles);
        }
        catch (Exception)
        {
            return new OidcAuthorizationRevalidationResult(false, []);
        }
    }

    public static bool ShouldRevalidate(ClaimsPrincipal principal) =>
        string.Equals(
            principal.FindFirstValue(SecurityClaimTypes.AuthSource),
            "oidc",
            StringComparison.Ordinal);

    public static void ApplyRoles(ClaimsIdentity identity, IReadOnlyList<string> roles)
    {
        foreach (var claim in identity.FindAll(ClaimTypes.Role).ToArray())
        {
            identity.RemoveClaim(claim);
        }

        foreach (var role in roles)
        {
            identity.AddClaim(new Claim(ClaimTypes.Role, role));
        }
    }
}

internal sealed class OidcCookiePrincipalValidator(
    OidcAuthorizationRevalidator revalidator,
    LocalAuthOptions authSettings)
{
    public async Task<bool> ValidateAsync(
        ClaimsPrincipal principal,
        AuthenticationProperties properties,
        CancellationToken cancellationToken)
    {
        if (!OidcAuthorizationRevalidator.ShouldRevalidate(principal))
        {
            return true;
        }

        var result = await revalidator.RevalidateAsync(principal, properties, authSettings, cancellationToken);
        if (!result.IsAuthorized)
        {
            return false;
        }

        foreach (var identity in principal.Identities.OfType<ClaimsIdentity>())
        {
            OidcAuthorizationRevalidator.ApplyRoles(identity, result.Roles);
        }

        return true;
    }
}

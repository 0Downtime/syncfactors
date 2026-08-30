using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using SyncFactors.Infrastructure;

namespace SyncFactors.Api;

internal static class OidcLogoutTokenStore
{
    public static void RetainIdToken(
        AuthenticationProperties properties,
        string? tokenEndpointIdToken,
        string? authorizationResponseIdToken)
    {
        var idToken = !string.IsNullOrWhiteSpace(tokenEndpointIdToken)
            ? tokenEndpointIdToken
            : authorizationResponseIdToken;

        properties.StoreTokens(string.IsNullOrWhiteSpace(idToken)
            ? []
            :
            [
                new AuthenticationToken
                {
                    Name = "id_token",
                    Value = idToken
                }
            ]);
    }
}

internal static class OidcConfigurationValidator
{
    public static void ValidateAuthority(
        OidcOptions options,
        bool oidcEnabled,
        bool isDevelopment)
    {
        if (!oidcEnabled || isDevelopment)
        {
            return;
        }

        if (!Uri.TryCreate(options.Authority, UriKind.Absolute, out var authority) ||
            !string.Equals(authority.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                "SyncFactors:Auth:Oidc:Authority must be an absolute HTTPS URL outside Development.");
        }
    }
}

internal sealed record OidcAuthorizationRevalidationResult(
    bool IsAuthorized,
    IReadOnlyList<string> Roles);

/// <summary>
/// Validates the reduced OIDC authorization snapshot stored in the protected cookie.
/// Role assignments originate only from claims in the validated ID token at sign-in.
/// Once the bounded snapshot lifetime expires, the cookie is rejected so a fresh OIDC
/// authentication must issue a new validated token and role snapshot.
/// </summary>
internal sealed class OidcAuthorizationRevalidator(TimeProvider timeProvider)
{
    private static readonly HashSet<string> AllowedOidcRoles =
    [
        SecurityRoles.Viewer,
        SecurityRoles.Operator,
        SecurityRoles.Admin
    ];

    public OidcAuthorizationRevalidationResult Revalidate(
        ClaimsPrincipal principal,
        LocalAuthOptions authSettings)
    {
        var subject = principal.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrWhiteSpace(subject))
        {
            return Unauthorized();
        }

        var roles = principal.FindAll(ClaimTypes.Role)
            .Select(claim => claim.Value)
            .Where(AllowedOidcRoles.Contains)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (roles.Length != 1)
        {
            return Unauthorized();
        }

        var validatedAtValue = principal.FindFirstValue(SecurityClaimTypes.OidcAuthorizationValidatedAt);
        if (!DateTimeOffset.TryParse(validatedAtValue, out var validatedAt))
        {
            return Unauthorized();
        }

        var now = timeProvider.GetUtcNow();
        if (validatedAt > now ||
            now - validatedAt >= authSettings.Oidc.GetAuthorizationRevalidationInterval())
        {
            return Unauthorized();
        }

        return new OidcAuthorizationRevalidationResult(true, roles);
    }

    public static bool ShouldRevalidate(ClaimsPrincipal principal) =>
        string.Equals(
            principal.FindFirstValue(SecurityClaimTypes.AuthSource),
            "oidc",
            StringComparison.Ordinal);

    private static OidcAuthorizationRevalidationResult Unauthorized() =>
        new(false, []);
}

internal sealed class OidcCookiePrincipalValidator(
    OidcAuthorizationRevalidator revalidator,
    LocalAuthOptions authSettings)
{
    public Task<bool> ValidateAsync(
        ClaimsPrincipal principal,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (!OidcAuthorizationRevalidator.ShouldRevalidate(principal))
        {
            return Task.FromResult(true);
        }

        var result = revalidator.Revalidate(principal, authSettings);
        return Task.FromResult(result.IsAuthorized);
    }
}

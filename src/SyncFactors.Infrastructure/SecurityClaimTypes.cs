namespace SyncFactors.Infrastructure;

public static class SecurityClaimTypes
{
    public const string AuthSource = "syncfactors_auth_source";
    public const string SessionIssuedAt = "syncfactors_session_issued_at";
    public const string OidcAuthorizationValidatedAt = "syncfactors_oidc_authorization_validated_at";
}

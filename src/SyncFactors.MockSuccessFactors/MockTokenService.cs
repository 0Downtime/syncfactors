namespace SyncFactors.MockSuccessFactors;

public static class MockTokenService
{
    public static TokenResponse? IssueToken(
        string grantType,
        string clientId,
        string clientSecret,
        string? companyId,
        MockAuthenticationOptions configured)
    {
        if (!string.Equals(grantType, "client_credentials", StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(clientId, configured.ClientId, StringComparison.Ordinal) ||
            !string.Equals(clientSecret, configured.ClientSecret, StringComparison.Ordinal) ||
            (!string.IsNullOrWhiteSpace(configured.CompanyId) &&
             !string.Equals(companyId, configured.CompanyId, StringComparison.Ordinal)))
        {
            return null;
        }

        return new TokenResponse(
            AccessToken: configured.BearerToken,
            TokenType: "Bearer",
            ExpiresIn: 3600);
    }
}

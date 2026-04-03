using System.Text.Json;

namespace SyncFactors.Infrastructure;

public sealed class SyncFactorsConfigurationValidator(SyncFactorsConfigurationLoader loader)
{
    public void Validate()
    {
        var sync = loader.GetSyncConfig();
        var mapping = loader.GetMappingConfig();
        var environmentName = Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT")
            ?? Environment.GetEnvironmentVariable("DOTNET_ENVIRONMENT")
            ?? "Production";
        var isDevelopment = string.Equals(environmentName, "Development", StringComparison.OrdinalIgnoreCase);

        if (!Uri.TryCreate(sync.SuccessFactors.BaseUrl, UriKind.Absolute, out _))
        {
            throw new InvalidOperationException("SyncFactors successFactors.baseUrl must be an absolute URI.");
        }

        if (string.IsNullOrWhiteSpace(sync.Ad.Server))
        {
            throw new InvalidOperationException("SyncFactors AD server must be configured.");
        }

        if (string.IsNullOrWhiteSpace(sync.Ad.DefaultActiveOu) ||
            string.IsNullOrWhiteSpace(sync.Ad.PrehireOu) ||
            string.IsNullOrWhiteSpace(sync.Ad.GraveyardOu))
        {
            throw new InvalidOperationException("SyncFactors AD active, prehire, and graveyard OUs must be configured.");
        }

        if (!string.Equals(sync.Ad.Transport.Mode, "ldaps", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(sync.Ad.Transport.Mode, "starttls", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("SyncFactors AD transport.mode must be either 'ldaps' or 'starttls'.");
        }

        if (!isDevelopment && !sync.Ad.Transport.RequireCertificateValidation)
        {
            throw new InvalidOperationException("SyncFactors AD transport.requireCertificateValidation must be enabled outside Development.");
        }

        if (!isDevelopment && !sync.Ad.Transport.RequireSigning)
        {
            throw new InvalidOperationException("SyncFactors AD transport.requireSigning must be enabled outside Development.");
        }

        if (sync.Sync.EnableBeforeStartDays < 0 || sync.Sync.DeletionRetentionDays < 0)
        {
            throw new InvalidOperationException("SyncFactors sync policy values must be non-negative.");
        }

        if (sync.Safety.MaxCreatesPerRun <= 0 || sync.Safety.MaxDisablesPerRun <= 0 || sync.Safety.MaxDeletionsPerRun <= 0)
        {
            throw new InvalidOperationException("SyncFactors safety thresholds must be positive.");
        }

        if (sync.SuccessFactors.Query.DeltaOverlapMinutes < 0)
        {
            throw new InvalidOperationException("SyncFactors successFactors.query.deltaOverlapMinutes must be non-negative.");
        }

        if (mapping.Mappings.Count == 0)
        {
            throw new InvalidOperationException("SyncFactors mapping config must contain at least one mapping.");
        }

        if (!mapping.Mappings.Any(mapping => mapping.Enabled))
        {
            throw new InvalidOperationException("SyncFactors mapping config must contain at least one enabled mapping.");
        }

        if (!isDevelopment)
        {
            ValidateNoProductionLiteralSecrets(loader.GetResolvedSyncConfigPath());
        }
    }

    private static void ValidateNoProductionLiteralSecrets(string configPath)
    {
        var document = JsonDocument.Parse(File.ReadAllText(configPath)).RootElement;
        var auth = document.GetRequiredObject("successFactors").GetRequiredObject("auth");
        var basic = auth.TryGetObject("basic", out var basicAuth) ? basicAuth : default;
        var oauth = auth.TryGetObject("oauth", out var oauthAuth) ? oauthAuth : default;
        var ad = document.GetRequiredObject("ad");

        var basicUsernameLiteral = basic.ValueKind == JsonValueKind.Object ? basic.TryGetString("username") : null;
        var basicPasswordLiteral = basic.ValueKind == JsonValueKind.Object ? basic.TryGetString("password") : null;
        var oauthClientIdLiteral = oauth.ValueKind == JsonValueKind.Object ? oauth.TryGetString("clientId") : null;
        var oauthClientSecretLiteral = oauth.ValueKind == JsonValueKind.Object ? oauth.TryGetString("clientSecret") : null;
        var adBindPasswordLiteral = ad.TryGetString("bindPassword");

        if (!string.IsNullOrWhiteSpace(basicUsernameLiteral))
        {
            throw new InvalidOperationException("Production config must not include a literal SuccessFactors username.");
        }

        if (!string.IsNullOrWhiteSpace(basicPasswordLiteral))
        {
            throw new InvalidOperationException("Production config must not include a literal SuccessFactors password.");
        }

        if (!string.IsNullOrWhiteSpace(oauthClientIdLiteral))
        {
            throw new InvalidOperationException("Production config must not include a literal SuccessFactors OAuth client ID.");
        }

        if (!string.IsNullOrWhiteSpace(oauthClientSecretLiteral))
        {
            throw new InvalidOperationException("Production config must not include a literal SuccessFactors OAuth client secret.");
        }

        if (!string.IsNullOrWhiteSpace(adBindPasswordLiteral))
        {
            throw new InvalidOperationException("Production config must not include a literal AD bind password.");
        }

        if (string.Equals(basicUsernameLiteral, "replace-me", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(basicPasswordLiteral, "replace-me", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(oauthClientIdLiteral, "replace-me", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(oauthClientSecretLiteral, "replace-me", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(adBindPasswordLiteral, "replace-me", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(adBindPasswordLiteral, "Replace-This-Password123!", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Production config contains placeholder credentials.");
        }
    }

}

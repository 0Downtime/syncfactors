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
            ValidateNoProductionLiteralSecrets(sync);
        }
    }

    private static void ValidateNoProductionLiteralSecrets(SyncFactorsConfigDocument sync)
    {
        if (!string.IsNullOrWhiteSpace(sync.SuccessFactors.Auth.Basic?.Username) &&
            !HasEnvironmentReference(sync.Secrets.SuccessFactorsUsernameEnv))
        {
            throw new InvalidOperationException("Production config must not include a literal SuccessFactors username.");
        }

        if (!string.IsNullOrWhiteSpace(sync.SuccessFactors.Auth.Basic?.Password))
        {
            throw new InvalidOperationException("Production config must not include a literal SuccessFactors password.");
        }

        if (!string.IsNullOrWhiteSpace(sync.SuccessFactors.Auth.OAuth?.ClientId) &&
            !HasEnvironmentReference(sync.Secrets.SuccessFactorsClientIdEnv))
        {
            throw new InvalidOperationException("Production config must not include a literal SuccessFactors OAuth client ID.");
        }

        if (!string.IsNullOrWhiteSpace(sync.SuccessFactors.Auth.OAuth?.ClientSecret))
        {
            throw new InvalidOperationException("Production config must not include a literal SuccessFactors OAuth client secret.");
        }

        if (!string.IsNullOrWhiteSpace(sync.Ad.BindPassword))
        {
            throw new InvalidOperationException("Production config must not include a literal AD bind password.");
        }

        if (string.Equals(sync.SuccessFactors.Auth.Basic?.Username, "replace-me", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(sync.SuccessFactors.Auth.Basic?.Password, "replace-me", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(sync.Ad.BindPassword, "replace-me", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(sync.Ad.BindPassword, "Replace-This-Password123!", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Production config contains placeholder credentials.");
        }
    }

    private static bool HasEnvironmentReference(string? environmentVariableName)
    {
        return !string.IsNullOrWhiteSpace(environmentVariableName);
    }
}

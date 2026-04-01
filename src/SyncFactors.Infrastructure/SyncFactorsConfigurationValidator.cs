namespace SyncFactors.Infrastructure;

public sealed class SyncFactorsConfigurationValidator(SyncFactorsConfigurationLoader loader)
{
    public void Validate()
    {
        var sync = loader.GetSyncConfig();
        var mapping = loader.GetMappingConfig();

        if (!Uri.TryCreate(sync.SuccessFactors.BaseUrl, UriKind.Absolute, out _))
        {
            throw new InvalidOperationException("SyncFactors successFactors.baseUrl must be an absolute URI.");
        }

        if (string.IsNullOrWhiteSpace(sync.Ad.Server))
        {
            throw new InvalidOperationException("SyncFactors AD server must be configured.");
        }

        if (string.IsNullOrWhiteSpace(sync.Ad.DefaultActiveOu) || string.IsNullOrWhiteSpace(sync.Ad.GraveyardOu))
        {
            throw new InvalidOperationException("SyncFactors AD OUs must be configured.");
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
    }
}

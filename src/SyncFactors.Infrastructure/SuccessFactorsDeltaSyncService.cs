using Microsoft.Extensions.Logging;
using SyncFactors.Domain;

namespace SyncFactors.Infrastructure;

public sealed class SuccessFactorsDeltaSyncService(
    SyncFactorsConfigurationLoader configLoader,
    IDeltaSyncStateStore stateStore,
    ILogger<SuccessFactorsDeltaSyncService> logger) : IDeltaSyncService
{
    public async Task<DeltaSyncWindow> GetWindowAsync(CancellationToken cancellationToken)
    {
        var config = configLoader.GetSyncConfig();
        var query = config.SuccessFactors.Query;
        if (!query.DeltaSyncEnabled || string.IsNullOrWhiteSpace(query.DeltaField))
        {
            return new DeltaSyncWindow(
                Enabled: false,
                HasCheckpoint: false,
                Filter: null,
                DeltaField: query.DeltaField,
                CheckpointUtc: null,
                EffectiveSinceUtc: null);
        }

        var previewBackedSources = GetPreviewBackedEnabledSources(config);
        if (previewBackedSources.Length > 0)
        {
            logger.LogWarning(
                "Delta sync is disabled because enabled mappings depend on preview-only fields. EntitySet={EntitySet} PreviewEntitySet={PreviewEntitySet} Sources={Sources}",
                query.EntitySet,
                config.SuccessFactors.PreviewQuery?.EntitySet,
                string.Join(", ", previewBackedSources));
            return new DeltaSyncWindow(
                Enabled: false,
                HasCheckpoint: false,
                Filter: null,
                DeltaField: query.DeltaField,
                CheckpointUtc: null,
                EffectiveSinceUtc: null);
        }

        var syncKey = BuildSyncKey(query);
        var checkpointUtc = await stateStore.GetCheckpointAsync(syncKey, cancellationToken);
        if (checkpointUtc is null)
        {
            logger.LogInformation(
                "Delta sync is enabled but no checkpoint exists yet. EntitySet={EntitySet} DeltaField={DeltaField}",
                query.EntitySet,
                query.DeltaField);
            return new DeltaSyncWindow(
                Enabled: true,
                HasCheckpoint: false,
                Filter: null,
                DeltaField: query.DeltaField,
                CheckpointUtc: null,
                EffectiveSinceUtc: null);
        }

        var effectiveSinceUtc = checkpointUtc.Value.AddMinutes(-Math.Max(0, query.DeltaOverlapMinutes)).ToUniversalTime();
        return new DeltaSyncWindow(
            Enabled: true,
            HasCheckpoint: true,
            Filter: $"{query.DeltaField} ge datetimeoffset'{FormatDateTimeOffsetLiteral(effectiveSinceUtc)}'",
            DeltaField: query.DeltaField,
            CheckpointUtc: checkpointUtc.Value.ToUniversalTime(),
            EffectiveSinceUtc: effectiveSinceUtc);
    }

    public async Task RecordSuccessfulRunAsync(DateTimeOffset checkpointUtc, CancellationToken cancellationToken)
    {
        var config = configLoader.GetSyncConfig();
        var query = config.SuccessFactors.Query;
        if (!query.DeltaSyncEnabled || string.IsNullOrWhiteSpace(query.DeltaField))
        {
            return;
        }

        if (GetPreviewBackedEnabledSources(config).Length > 0)
        {
            return;
        }

        checkpointUtc = checkpointUtc.ToUniversalTime();
        await stateStore.SaveCheckpointAsync(BuildSyncKey(query), checkpointUtc, cancellationToken);
        logger.LogInformation(
            "Advanced SuccessFactors delta sync checkpoint. EntitySet={EntitySet} DeltaField={DeltaField} CheckpointUtc={CheckpointUtc}",
            query.EntitySet,
            query.DeltaField,
            checkpointUtc);
    }

    private static string BuildSyncKey(SuccessFactorsQueryConfig query)
    {
        return string.Join("|",
            query.EntitySet,
            query.IdentityField,
            query.DeltaField,
            query.BaseFilter ?? string.Empty,
            query.AsOfDate ?? string.Empty);
    }

    private static string FormatDateTimeOffsetLiteral(DateTimeOffset value)
    {
        return value.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'");
    }

    private string[] GetPreviewBackedEnabledSources(SyncFactorsConfigDocument config)
    {
        var previewQuery = config.SuccessFactors.PreviewQuery;
        if (previewQuery is null ||
            string.Equals(config.SuccessFactors.Query.EntitySet, previewQuery.EntitySet, StringComparison.OrdinalIgnoreCase))
        {
            return [];
        }

        return configLoader.GetMappingConfig().Mappings
            .Where(mapping => mapping.Enabled)
            .Select(mapping => mapping.Source)
            .Where(IsPreviewBackedSource)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static bool IsPreviewBackedSource(string source)
    {
        if (string.IsNullOrWhiteSpace(source))
        {
            return false;
        }

        string[] previewMarkers =
        [
            "personIdExternal",
            "personId",
            "perPersonUuid",
            "personalInfoNav",
            "emailNav",
            "phoneNav",
            "employmentNav[0].userNav",
            "personEmpTerminationInfoNav",
            "displayName"
        ];

        return previewMarkers.Any(marker => source.Contains(marker, StringComparison.OrdinalIgnoreCase));
    }
}

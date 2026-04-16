using Microsoft.Extensions.Logging;
using SyncFactors.Domain;

namespace SyncFactors.Infrastructure;

public sealed class SuccessFactorsDeltaSyncService(
    SyncFactorsConfigurationLoader configLoader,
    IDeltaSyncStateStore stateStore,
    TimeProvider timeProvider,
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

        var syncKey = BuildSyncKey(query, config.Sync.EnableBeforeStartDays);
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
        var deltaFilter = BuildDeltaFilter(query, effectiveSinceUtc, checkpointUtc.Value.ToUniversalTime(), config.Sync.EnableBeforeStartDays);
        return new DeltaSyncWindow(
            Enabled: true,
            HasCheckpoint: true,
            Filter: deltaFilter,
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
        await stateStore.SaveCheckpointAsync(BuildSyncKey(query, config.Sync.EnableBeforeStartDays), checkpointUtc, cancellationToken);
        logger.LogInformation(
            "Advanced SuccessFactors delta sync checkpoint. EntitySet={EntitySet} DeltaField={DeltaField} CheckpointUtc={CheckpointUtc}",
            query.EntitySet,
            query.DeltaField,
            checkpointUtc);
    }

    private static string BuildSyncKey(SuccessFactorsQueryConfig query, int enableBeforeStartDays)
    {
        return string.Join("|",
            query.EntitySet,
            query.IdentityField,
            query.DeltaField,
            query.OnboardingDateField,
            query.BaseFilter ?? string.Empty,
            query.AsOfDate ?? string.Empty,
            enableBeforeStartDays);
    }

    private string BuildDeltaFilter(
        SuccessFactorsQueryConfig query,
        DateTimeOffset effectiveSinceUtc,
        DateTimeOffset checkpointUtc,
        int enableBeforeStartDays)
    {
        var filters = new List<string>
        {
            $"{query.DeltaField} ge datetimeoffset'{FormatDateTimeOffsetLiteral(effectiveSinceUtc)}'"
        };

        if (string.IsNullOrWhiteSpace(query.OnboardingDateField))
        {
            return filters[0];
        }

        var todayUtc = timeProvider.GetUtcNow().UtcDateTime.Date;
        var checkpointDateUtc = checkpointUtc.UtcDateTime.Date;
        var onboardingField = query.OnboardingDateField;

        if (todayUtc > checkpointDateUtc)
        {
            filters.Add(
                $"{onboardingField} gt datetime'{FormatDateLiteral(checkpointDateUtc)}' and {onboardingField} le datetime'{FormatDateLiteral(todayUtc)}'");
        }

        if (enableBeforeStartDays > 0)
        {
            var priorNearStartHorizonUtc = checkpointDateUtc.AddDays(enableBeforeStartDays);
            var currentNearStartHorizonUtc = todayUtc.AddDays(enableBeforeStartDays);
            if (currentNearStartHorizonUtc > priorNearStartHorizonUtc)
            {
                filters.Add(
                    $"{onboardingField} gt datetime'{FormatDateLiteral(priorNearStartHorizonUtc)}' and {onboardingField} le datetime'{FormatDateLiteral(currentNearStartHorizonUtc)}'");
            }
        }

        return filters.Count == 1
            ? filters[0]
            : string.Join(" or ", filters.Select(filter => $"({filter})"));
    }

    private static string FormatDateTimeOffsetLiteral(DateTimeOffset value)
    {
        return value.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'");
    }

    private static string FormatDateLiteral(DateTime value)
    {
        return value.ToString("yyyy-MM-dd'T'HH:mm:ss");
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

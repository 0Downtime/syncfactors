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
        var query = configLoader.GetSyncConfig().SuccessFactors.Query;
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

    public async Task RecordSuccessfulRunAsync(CancellationToken cancellationToken)
    {
        var query = configLoader.GetSyncConfig().SuccessFactors.Query;
        if (!query.DeltaSyncEnabled || string.IsNullOrWhiteSpace(query.DeltaField))
        {
            return;
        }

        var checkpointUtc = timeProvider.GetUtcNow().ToUniversalTime();
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
}

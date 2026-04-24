using SyncFactors.Contracts;

namespace SyncFactors.Domain;

public sealed class NullRunCaptureMetadataProvider : IRunCaptureMetadataProvider
{
    public static NullRunCaptureMetadataProvider Instance { get; } = new();

    private NullRunCaptureMetadataProvider()
    {
    }

    public RunCaptureMetadata Create(string runId, bool dryRun, string syncScope) =>
        new(
            SchemaVersion: 1,
            RunId: runId,
            DryRun: dryRun,
            SyncScope: syncScope,
            SyncConfig: null,
            MappingConfig: null);
}

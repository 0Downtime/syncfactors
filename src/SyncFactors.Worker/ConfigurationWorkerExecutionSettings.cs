using SyncFactors.Domain;
using SyncFactors.Infrastructure;

namespace SyncFactors.Worker;

public sealed class ConfigurationWorkerExecutionSettings(
    SyncFactorsConfigurationLoader configLoader) : IWorkerExecutionSettings
{
    public int GetMaxDegreeOfParallelism() => configLoader.GetSyncConfig().Sync.MaxDegreeOfParallelism;
}

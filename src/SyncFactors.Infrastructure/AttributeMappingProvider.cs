using SyncFactors.Domain;
using Microsoft.Extensions.Logging;

namespace SyncFactors.Infrastructure;

public sealed class AttributeMappingProvider(
    SyncFactorsConfigurationLoader configLoader,
    ILogger<AttributeMappingProvider> logger) : IAttributeMappingProvider
{
    public IReadOnlyList<AttributeMapping> GetEnabledMappings()
    {
        var mappings = configLoader.GetMappingConfig().Mappings;
        var enabledMappings = mappings
            .Where(mapping => mapping.Enabled)
            .Select(mapping => new AttributeMapping(
                Source: mapping.Source,
                Target: mapping.Target,
                Required: mapping.Required,
                Transform: mapping.Transform))
            .ToArray();

        logger.LogDebug(
            "Loaded attribute mappings. Enabled={EnabledCount} Disabled={DisabledCount} EnabledTargets={EnabledTargets} DisabledTargets={DisabledTargets}",
            enabledMappings.Length,
            Math.Max(0, mappings.Count - enabledMappings.Length),
            string.Join(", ", enabledMappings.Select(mapping => mapping.Target)),
            string.Join(", ", mappings.Where(mapping => !mapping.Enabled).Select(mapping => mapping.Target)));

        return enabledMappings;
    }
}

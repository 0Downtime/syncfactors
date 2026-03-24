using SyncFactors.Domain;

namespace SyncFactors.Infrastructure;

public sealed class AttributeMappingProvider(SyncFactorsConfigurationLoader configLoader) : IAttributeMappingProvider
{
    public IReadOnlyList<AttributeMapping> GetEnabledMappings()
    {
        return configLoader.GetMappingConfig().Mappings
            .Where(mapping => mapping.Enabled)
            .Select(mapping => new AttributeMapping(
                Source: mapping.Source,
                Target: mapping.Target,
                Required: mapping.Required,
                Transform: mapping.Transform))
            .ToArray();
    }
}

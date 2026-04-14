using SyncFactors.Domain;

namespace SyncFactors.Infrastructure;

public sealed class ConfiguredEmailAddressPolicy(SyncFactorsConfigurationLoader configLoader) : IEmailAddressPolicy
{
    public string BuildEmailAddress(string localPart)
    {
        var upnSuffix = configLoader.GetSyncConfig().Ad.UpnSuffix;
        return DirectoryIdentityFormatter.BuildEmailAddress(localPart, upnSuffix);
    }
}

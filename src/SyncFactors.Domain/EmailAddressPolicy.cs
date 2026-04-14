namespace SyncFactors.Domain;

public interface IEmailAddressPolicy
{
    string BuildEmailAddress(string localPart);
}

public sealed class DefaultEmailAddressPolicy : IEmailAddressPolicy
{
    public string BuildEmailAddress(string localPart)
    {
        return DirectoryIdentityFormatter.BuildEmailAddress(localPart);
    }
}

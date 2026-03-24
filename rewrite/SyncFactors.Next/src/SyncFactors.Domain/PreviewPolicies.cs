using SyncFactors.Contracts;

namespace SyncFactors.Domain;

public sealed class IdentityMatcher : IIdentityMatcher
{
    public IdentityMatchResult Match(WorkerSnapshot worker, DirectoryUserSnapshot? directoryUser)
    {
        if (!string.IsNullOrWhiteSpace(directoryUser?.SamAccountName))
        {
            return new IdentityMatchResult(
                Bucket: "updates",
                MatchedExistingUser: true,
                SamAccountName: directoryUser.SamAccountName!,
                Reason: "Native preview matched an existing directory account.",
                OperatorActionSummary: "Update account preview");
        }

        return new IdentityMatchResult(
            Bucket: "creates",
            MatchedExistingUser: false,
            SamAccountName: $"{worker.PreferredName}.{worker.LastName}".ToLowerInvariant(),
            Reason: "Native preview planned a new directory account.",
            OperatorActionSummary: "Create account preview");
    }
}

public sealed class AttributeDiffService : IAttributeDiffService
{
    public IReadOnlyList<AttributeChange> BuildDiff(WorkerSnapshot worker, DirectoryUserSnapshot? directoryUser)
    {
        var proposedDisplayName = $"{worker.PreferredName} {worker.LastName}";
        var currentDisplayName = string.IsNullOrWhiteSpace(directoryUser?.DisplayName)
            ? "(unset)"
            : directoryUser!.DisplayName!;

        return
        [
            new AttributeChange(
                Attribute: "displayName",
                Source: "preferredName",
                Before: currentDisplayName,
                After: proposedDisplayName,
                Changed: currentDisplayName != proposedDisplayName)
        ];
    }
}

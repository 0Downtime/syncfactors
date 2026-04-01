using SyncFactors.Contracts;

namespace SyncFactors.Domain;

public sealed class DirectoryMutationCommandBuilder : IDirectoryMutationCommandBuilder
{
    public DirectoryMutationCommand Build(PlannedWorkerAction plan)
    {
        var action = plan.Identity.MatchedExistingUser ? "UpdateUser" : "CreateUser";
        var samAccountName = plan.Identity.SamAccountName;
        var displayName = plan.DirectoryUser.SamAccountName ?? samAccountName;
        var userPrincipalName = plan.Identity.MatchedExistingUser
            ? plan.DirectoryUser.Attributes.TryGetValue("UserPrincipalName", out var existingUserPrincipalName) && !string.IsNullOrWhiteSpace(existingUserPrincipalName)
                ? existingUserPrincipalName
                : plan.ProposedEmailAddress
            : plan.ProposedEmailAddress;
        var mail = plan.Identity.MatchedExistingUser
            ? plan.DirectoryUser.Attributes.TryGetValue("mail", out var existingMail) && !string.IsNullOrWhiteSpace(existingMail)
                ? existingMail
                : plan.ProposedEmailAddress
            : plan.ProposedEmailAddress;

        return new DirectoryMutationCommand(
            Action: action,
            WorkerId: plan.Worker.WorkerId,
            ManagerId: plan.Worker.Attributes.TryGetValue("managerId", out var managerId) ? managerId : null,
            ManagerDistinguishedName: plan.ManagerDistinguishedName,
            SamAccountName: samAccountName,
            UserPrincipalName: userPrincipalName,
            Mail: mail,
            TargetOu: plan.Worker.TargetOu,
            DisplayName: displayName,
            EnableAccount: plan.DirectoryUser.Enabled ?? true,
            Attributes: BuildAttributes(plan.AttributeChanges));
    }

    public DirectoryMutationCommand Build(WorkerSnapshot worker, WorkerPreviewResult preview)
    {
        var action = preview.Buckets.Contains("creates", StringComparer.OrdinalIgnoreCase) ? "CreateUser" : "UpdateUser";
        var samAccountName = preview.SamAccountName ?? throw new InvalidOperationException("Preview did not produce a SAM account name.");
        var displayName = GetPreviewAttributeValue(preview, "displayName") ?? samAccountName;
        var emailAddress = GetPreviewAttributeValue(preview, "UserPrincipalName")
            ?? GetPreviewAttributeValue(preview, "userPrincipalName")
            ?? GetPreviewAttributeValue(preview, "mail")
            ?? DirectoryIdentityFormatter.BuildEmailAddress(
                DirectoryIdentityFormatter.BuildBaseEmailLocalPart(worker.PreferredName, worker.LastName));
        var mailAddress = GetPreviewAttributeValue(preview, "mail") ?? emailAddress;

        return new DirectoryMutationCommand(
            Action: action,
            WorkerId: worker.WorkerId,
            ManagerId: worker.Attributes.TryGetValue("managerId", out var managerId) ? managerId : null,
            ManagerDistinguishedName: preview.ManagerDistinguishedName,
            SamAccountName: samAccountName,
            UserPrincipalName: emailAddress,
            Mail: mailAddress,
            TargetOu: preview.TargetOu ?? worker.TargetOu,
            DisplayName: displayName,
            EnableAccount: preview.ProposedEnable ?? true,
            Attributes: BuildAttributes(preview.DiffRows.Select(row => new AttributeChange(row.Attribute, row.Source, row.Before, row.After, row.Changed)).ToArray()));
    }

    private static IReadOnlyDictionary<string, string?> BuildAttributes(IReadOnlyList<AttributeChange> changes)
    {
        var attributes = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in changes.Where(row => row.Changed))
        {
            attributes[row.Attribute] = string.Equals(row.After, "(unset)", StringComparison.Ordinal)
                ? null
                : row.After;
        }

        return attributes;
    }

    private static string? GetPreviewAttributeValue(WorkerPreviewResult preview, string attributeName)
    {
        var row = preview.DiffRows.FirstOrDefault(diffRow =>
            string.Equals(diffRow.Attribute, attributeName, StringComparison.OrdinalIgnoreCase));
        if (row is null)
        {
            return null;
        }

        return string.Equals(row.After, "(unset)", StringComparison.Ordinal)
            ? null
            : row.After;
    }
}

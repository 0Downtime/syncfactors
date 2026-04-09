using SyncFactors.Contracts;

namespace SyncFactors.Domain;

public sealed class DirectoryMutationCommandBuilder : IDirectoryMutationCommandBuilder
{
    public DirectoryMutationCommand Build(PlannedWorkerAction plan)
    {
        var action = plan.PrimaryAction;
        var samAccountName = plan.Identity.SamAccountName;
        var commonName = samAccountName;
        var displayName = DirectoryIdentityFormatter.BuildDisplayName(plan.Worker.PreferredName, plan.Worker.LastName);
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
            CommonName: commonName,
            UserPrincipalName: userPrincipalName,
            Mail: mail,
            TargetOu: plan.TargetOu,
            DisplayName: displayName,
            CurrentDistinguishedName: plan.DirectoryUser.DistinguishedName,
            EnableAccount: plan.TargetEnabled,
            Operations: plan.Operations,
            Attributes: BuildAttributes(plan.AttributeChanges));
    }

    public DirectoryMutationCommand Build(WorkerSnapshot worker, WorkerPreviewResult preview)
    {
        var action = ResolvePrimaryAction(preview);
        var samAccountName = preview.SamAccountName ?? throw new InvalidOperationException("Preview did not produce a SAM account name.");
        var commonName = samAccountName;
        var displayName = GetPreviewAttributeValue(preview, "displayName")
            ?? DirectoryIdentityFormatter.BuildDisplayName(worker.PreferredName, worker.LastName);
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
            CommonName: commonName,
            UserPrincipalName: emailAddress,
            Mail: mailAddress,
            TargetOu: preview.TargetOu ?? worker.TargetOu,
            DisplayName: displayName,
            CurrentDistinguishedName: preview.CurrentDistinguishedName,
            EnableAccount: preview.ProposedEnable ?? true,
            Operations: preview.Entries
                .SelectMany(entry => GetPreviewOperations(entry.Item))
                .Distinct()
                .ToArray(),
            Attributes: BuildAttributes(preview.DiffRows.Select(row => new AttributeChange(row.Attribute, row.Source, row.Before, row.After, row.Changed)).ToArray()));
    }

    private static string ResolvePrimaryAction(WorkerPreviewResult preview)
    {
        var bucket = preview.Buckets.FirstOrDefault() ?? "updates";
        return bucket switch
        {
            "creates" => "CreateUser",
            "updates" => "UpdateUser",
            "enables" => "EnableUser",
            "disables" => "DisableUser",
            "graveyardMoves" => "MoveUser",
            _ => "UpdateUser"
        };
    }

    private static IEnumerable<DirectoryOperation> GetPreviewOperations(System.Text.Json.JsonElement item)
    {
        if (!item.TryGetProperty("operations", out var operations) || operations.ValueKind != System.Text.Json.JsonValueKind.Array)
        {
            yield break;
        }

        foreach (var operation in operations.EnumerateArray())
        {
            var kind = operation.TryGetProperty("kind", out var kindValue) ? kindValue.GetString() : null;
            if (string.IsNullOrWhiteSpace(kind))
            {
                continue;
            }

            var targetOu = operation.TryGetProperty("targetOu", out var targetOuValue)
                ? targetOuValue.GetString()
                : null;
            yield return new DirectoryOperation(kind, targetOu);
        }
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

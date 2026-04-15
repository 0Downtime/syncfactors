using Microsoft.Extensions.Logging.Abstractions;
using SyncFactors.Contracts;
using SyncFactors.Domain;

namespace SyncFactors.Domain.Tests;

public sealed class DirectoryMutationCommandBuilderTests
{
    [Fact]
    public async Task Build_FullSyncAndPreviewApply_ProduceEquivalentUpdatePayloads()
    {
        var worker = new WorkerSnapshot(
            WorkerId: "00037",
            PreferredName: "David",
            LastName: "LaRussa",
            Department: "10002 Corporate - Direct",
            TargetOu: "OU=LabUsers,DC=example,DC=com",
            IsPrehire: false,
            Attributes: new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
            {
                ["personIdExternal"] = "00051",
                ["firstName"] = "David",
                ["lastName"] = "LaRussa",
                ["email"] = "david.larussa@example.com",
                ["costCenterId"] = "10002",
                ["costCenterDescription"] = "Corporate - Direct",
                ["company"] = "Spire Missouri Inc",
                ["location"] = "STL - 700 Market",
                ["jobTitle"] = "Coord, Damage Prevention",
                ["employeeType"] = "61920",
                ["officeLocationAddress"] = "700 Market St",
                ["officeLocationCity"] = "Saint Louis",
                ["officeLocationZipCode"] = "63101",
                ["managerId"] = "90001"
            });

        var directoryUser = new DirectoryUserSnapshot(
            SamAccountName: "00037",
            DistinguishedName: "CN=LaRussa\\, David,OU=LabUsers,DC=example,DC=com",
            Enabled: true,
            DisplayName: "LaRussa, David",
            Attributes: new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
            {
                ["displayName"] = "LaRussa, David",
                ["GivenName"] = "David",
                ["Surname"] = "LaRussa",
                ["UserPrincipalName"] = "david.larussa@example.com",
                ["mail"] = "david.larussa@example.com",
                ["department"] = "10002 Corporate - Direct",
                ["company"] = "Spire Missouri Inc",
                ["physicalDeliveryOfficeName"] = "STL - 700 Market",
                ["title"] = "Coord, Damage Prevention",
                ["streetAddress"] = "700 Market St",
                ["l"] = "Saint Louis",
                ["postalCode"] = "63101",
                ["employeeType"] = "(unset)"
            });

        var diffService = new AttributeDiffService(
            new StubAttributeMappingProvider(),
            new NullWorkerPreviewLogWriter(),
            NullLogger<AttributeDiffService>.Instance);

        var attributeChanges = await diffService.BuildDiffAsync(
            worker,
            directoryUser,
            proposedEmailAddress: "david.larussa@example.com",
            logPath: null,
            cancellationToken: CancellationToken.None);

        var plan = new PlannedWorkerAction(
            Worker: worker,
            DirectoryUser: directoryUser,
            Identity: new IdentityMatchResult("updates", MatchedExistingUser: true, SamAccountName: "00037", Reason: null, OperatorActionSummary: null),
            ManagerDistinguishedName: "CN=Manager,OU=LabUsers,DC=example,DC=com",
            ProposedEmailAddress: "david.larussa@example.com",
            AttributeChanges: attributeChanges,
            MissingSourceAttributes: [],
            Bucket: "updates",
            CurrentOu: "OU=LabUsers,DC=example,DC=com",
            TargetOu: "OU=LabUsers,DC=example,DC=com",
            CurrentEnabled: true,
            TargetEnabled: true,
            PrimaryAction: "UpdateUser",
            Operations: [new DirectoryOperation("UpdateUser")],
            ReviewCategory: null,
            ReviewCaseType: null,
            Reason: null,
            CanAutoApply: true);

        var preview = new WorkerPreviewResult(
            ReportPath: null,
            RunId: "preview-00037",
            PreviousRunId: null,
            Fingerprint: "fingerprint-00037",
            Mode: "Preview",
            Status: "Planned",
            ErrorMessage: null,
            ArtifactType: "WorkerPreview",
            SuccessFactorsAuth: "MockSuccessFactors",
            WorkerId: worker.WorkerId,
            Buckets: ["updates"],
            MatchedExistingUser: true,
            ReviewCategory: null,
            ReviewCaseType: null,
            Reason: null,
            OperatorActionSummary: null,
            SamAccountName: "00037",
            ManagerDistinguishedName: "CN=Manager,OU=LabUsers,DC=example,DC=com",
            TargetOu: worker.TargetOu,
            CurrentDistinguishedName: directoryUser.DistinguishedName,
            CurrentEnabled: true,
            ProposedEnable: true,
            OperationSummary: null,
            DiffRows: attributeChanges
                .Select(change => new DiffRow(change.Attribute, change.Source, change.Before, change.After, change.Changed))
                .ToArray(),
            SourceAttributes: [],
            UsedSourceAttributes: [],
            UnusedSourceAttributes: [],
            MissingSourceAttributes: [],
            Entries:
            [
                new WorkerPreviewEntry(
                    Bucket: "updates",
                    Item: System.Text.Json.JsonDocument.Parse("""{"operations":[{"kind":"UpdateUser"}]}""").RootElement.Clone())
            ]);

        var builder = new DirectoryMutationCommandBuilder();
        var fullSyncCommand = builder.Build(plan);
        var previewApplyCommand = builder.Build(worker, preview);

        Assert.Equal(fullSyncCommand.Action, previewApplyCommand.Action);
        Assert.Equal(fullSyncCommand.WorkerId, previewApplyCommand.WorkerId);
        Assert.Equal(fullSyncCommand.ManagerId, previewApplyCommand.ManagerId);
        Assert.Equal(fullSyncCommand.ManagerDistinguishedName, previewApplyCommand.ManagerDistinguishedName);
        Assert.Equal(fullSyncCommand.SamAccountName, previewApplyCommand.SamAccountName);
        Assert.Equal(fullSyncCommand.CommonName, previewApplyCommand.CommonName);
        Assert.Equal(fullSyncCommand.UserPrincipalName, previewApplyCommand.UserPrincipalName);
        Assert.Equal(fullSyncCommand.Mail, previewApplyCommand.Mail);
        Assert.Equal(fullSyncCommand.TargetOu, previewApplyCommand.TargetOu);
        Assert.Equal(fullSyncCommand.DisplayName, previewApplyCommand.DisplayName);
        Assert.Equal(fullSyncCommand.CurrentDistinguishedName, previewApplyCommand.CurrentDistinguishedName);
        Assert.Equal(fullSyncCommand.EnableAccount, previewApplyCommand.EnableAccount);
        Assert.Equal(
            fullSyncCommand.Attributes.OrderBy(item => item.Key, StringComparer.OrdinalIgnoreCase),
            previewApplyCommand.Attributes.OrderBy(item => item.Key, StringComparer.OrdinalIgnoreCase));
        Assert.Equal("00037", fullSyncCommand.CommonName);
        Assert.Equal("LaRussa, David", fullSyncCommand.DisplayName);
    }

    [Fact]
    public void Build_FromPlan_ThrowsWhenMappedAttributeExceedsAdSchemaLimit()
    {
        const string oversizedDepartment = "20921 MOW - Distribution - Maintenance & Construction - SW Missouri";
        var worker = new WorkerSnapshot(
            WorkerId: "20921",
            PreferredName: "Terry",
            LastName: "Example",
            Department: oversizedDepartment,
            TargetOu: "OU=LabUsers,DC=example,DC=com",
            IsPrehire: false,
            Attributes: new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
            {
                ["managerId"] = "90001"
            });

        var plan = new PlannedWorkerAction(
            Worker: worker,
            DirectoryUser: new DirectoryUserSnapshot(
                SamAccountName: "20921",
                DistinguishedName: "CN=20921,OU=LabUsers,DC=example,DC=com",
                Enabled: true,
                DisplayName: "Example, Terry",
                Attributes: new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)),
            Identity: new IdentityMatchResult("updates", MatchedExistingUser: true, SamAccountName: "20921", Reason: null, OperatorActionSummary: null),
            ManagerDistinguishedName: null,
            ProposedEmailAddress: "terry.example@example.com",
            AttributeChanges:
            [
                new AttributeChange("department", "Concat(costCenterId, costCenterDescription)", "(unset)", oversizedDepartment, true)
            ],
            MissingSourceAttributes: [],
            Bucket: "updates",
            CurrentOu: "OU=LabUsers,DC=example,DC=com",
            TargetOu: "OU=LabUsers,DC=example,DC=com",
            CurrentEnabled: true,
            TargetEnabled: true,
            PrimaryAction: "UpdateUser",
            Operations: [new DirectoryOperation("UpdateUser")],
            ReviewCategory: null,
            ReviewCaseType: null,
            Reason: null,
            CanAutoApply: true);

        var error = Assert.Throws<InvalidOperationException>(() => new DirectoryMutationCommandBuilder().Build(plan));

        Assert.Contains("department", error.Message, StringComparison.Ordinal);
        Assert.Contains("67", error.Message, StringComparison.Ordinal);
        Assert.Contains("64", error.Message, StringComparison.Ordinal);
    }

    private sealed class StubAttributeMappingProvider : IAttributeMappingProvider
    {
        public IReadOnlyList<AttributeMapping> GetEnabledMappings() =>
        [
            new("personIdExternal", "employeeID", Required: true, Transform: "Trim"),
            new("firstName", "GivenName", Required: true, Transform: "Trim"),
            new("lastName", "Surname", Required: true, Transform: "Trim"),
            new("email", "UserPrincipalName", Required: true, Transform: "Lower"),
            new("email", "mail", Required: false, Transform: "Lower"),
            new("Concat(costCenterId, costCenterDescription)", "department", Required: false, Transform: "Trim"),
            new("company", "company", Required: false, Transform: "TrimStripCommasPeriods"),
            new("location", "physicalDeliveryOfficeName", Required: false, Transform: "Trim"),
            new("jobTitle", "title", Required: false, Transform: "Trim"),
            new("employeeType", "employeeType", Required: false, Transform: "Trim"),
            new("officeLocationAddress", "streetAddress", Required: false, Transform: "Trim"),
            new("officeLocationCity", "l", Required: false, Transform: "Trim"),
            new("officeLocationZipCode", "postalCode", Required: false, Transform: "Trim")
        ];
    }

    private sealed class NullWorkerPreviewLogWriter : IWorkerPreviewLogWriter
    {
        public string CreateLogPath(string workerId, DateTimeOffset startedAt)
        {
            _ = workerId;
            _ = startedAt;
            return string.Empty;
        }

        public Task AppendAsync(string logPath, WorkerPreviewLogEntry entry, CancellationToken cancellationToken)
        {
            _ = logPath;
            _ = entry;
            _ = cancellationToken;
            return Task.CompletedTask;
        }
    }
}

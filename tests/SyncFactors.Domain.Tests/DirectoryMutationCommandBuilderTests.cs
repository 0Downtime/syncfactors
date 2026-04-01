using SyncFactors.Contracts;
using SyncFactors.Domain;

namespace SyncFactors.Domain.Tests;

public sealed class DirectoryMutationCommandBuilderTests
{
    [Fact]
    public void Build_ForMatchedExistingUser_PreservesCurrentUpnAndMail()
    {
        var builder = new DirectoryMutationCommandBuilder();
        var worker = new WorkerSnapshot(
            WorkerId: "10001",
            PreferredName: "John",
            LastName: "Smith",
            Department: "IT",
            TargetOu: "OU=LabUsers,DC=example,DC=com",
            IsPrehire: false,
            Attributes: new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase));
        var directoryUser = new DirectoryUserSnapshot(
            SamAccountName: "10001",
            DistinguishedName: "CN=Smith\\, John,OU=LabUsers,DC=example,DC=com",
            Enabled: true,
            DisplayName: "Smith, John",
            Attributes: new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
            {
                ["UserPrincipalName"] = "existing.upn@spireenergy.com",
                ["mail"] = "existing.mail@spireenergy.com"
            });
        var plan = new PlannedWorkerAction(
            Worker: worker,
            DirectoryUser: directoryUser,
            Identity: new IdentityMatchResult("updates", true, "10001", null, null),
            ManagerDistinguishedName: null,
            ProposedEmailAddress: "john.smith2@spireenergy.com",
            AttributeChanges: [],
            MissingSourceAttributes: [],
            Bucket: "updates",
            ReviewCategory: null,
            ReviewCaseType: null,
            Reason: null,
            CanAutoApply: true);

        var command = builder.Build(plan);

        Assert.Equal("UpdateUser", command.Action);
        Assert.Equal("existing.upn@spireenergy.com", command.UserPrincipalName);
        Assert.Equal("existing.mail@spireenergy.com", command.Mail);
    }
}

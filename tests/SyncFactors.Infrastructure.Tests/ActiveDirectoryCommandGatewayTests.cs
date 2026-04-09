using System.DirectoryServices.Protocols;
using System.Reflection;
using SyncFactors.Contracts;
using SyncFactors.Infrastructure;

namespace SyncFactors.Infrastructure.Tests;

public sealed class ActiveDirectoryCommandGatewayTests
{
    [Fact]
    public void GetParentDistinguishedName_IgnoresEscapedCommaInCommonName()
    {
        var method = typeof(ActiveDirectoryCommandGateway).GetMethod("GetParentDistinguishedName", BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);

        var parentDn = Assert.IsType<string>(method!.Invoke(null, ["CN=Brien\\, Christopher,OU=IT,DC=example,DC=com"]));

        Assert.Equal("OU=IT,DC=example,DC=com", parentDn);
    }

    [Fact]
    public void BuildUpdateRenameFailureDetails_IncludesRenameContext()
    {
        var method = typeof(ActiveDirectoryCommandGateway).GetMethod("BuildUpdateRenameFailureDetails", BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);

        var command = new DirectoryMutationCommand(
            Action: "UpdateUser",
            WorkerId: "00004",
            ManagerId: null,
            ManagerDistinguishedName: null,
            SamAccountName: "00004",
            CommonName: "00004",
            UserPrincipalName: "tanya.willislivers@example.com",
            Mail: "tanya.willislivers@example.com",
            TargetOu: "OU=Users,DC=example,DC=com",
            DisplayName: "Willis Livers, Tanya",
            CurrentDistinguishedName: "CN=00004,OU=Users,DC=example,DC=com",
            EnableAccount: true,
            Operations: [new SyncFactors.Contracts.DirectoryOperation("UpdateUser")],
            Attributes: new Dictionary<string, string?>());

        var details = Assert.IsType<string>(method!.Invoke(null, [command, "CN=00004,OU=Users,DC=example,DC=com", "00004"]));

        Assert.Contains("Step=RenameUser", details, StringComparison.Ordinal);
        Assert.Contains("WorkerId=00004", details, StringComparison.Ordinal);
        Assert.Contains("CurrentCn=00004", details, StringComparison.Ordinal);
        Assert.Contains("DesiredCn=00004", details, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildUpdateModifyFailureDetails_ListsModifiedAttributes()
    {
        var method = typeof(ActiveDirectoryCommandGateway).GetMethod("BuildUpdateModifyFailureDetails", BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);

        var command = new DirectoryMutationCommand(
            Action: "UpdateUser",
            WorkerId: "00004",
            ManagerId: "12345",
            ManagerDistinguishedName: null,
            SamAccountName: "00004",
            CommonName: "00004",
            UserPrincipalName: "tanya.willislivers@example.com",
            Mail: "tanya.willislivers@example.com",
            TargetOu: "OU=Users,DC=example,DC=com",
            DisplayName: "Willis Livers, Tanya",
            CurrentDistinguishedName: "CN=Willis Livers\\, Tanya,OU=Users,DC=example,DC=com",
            EnableAccount: true,
            Operations: [new SyncFactors.Contracts.DirectoryOperation("UpdateUser")],
            Attributes: new Dictionary<string, string?>());

        var modifications = new DirectoryAttributeModificationCollection
        {
            CreateModification("displayName"),
            CreateModification("streetAddress"),
            CreateModification("l")
        };

        var details = Assert.IsType<string>(method!.Invoke(null, [command, "CN=Willis Livers\\, Tanya,OU=Users,DC=example,DC=com", modifications]));

        Assert.Contains("Step=ModifyAttributes", details, StringComparison.Ordinal);
        Assert.Contains("Attributes=displayName,streetAddress,l", details, StringComparison.Ordinal);
        Assert.Contains("ManagerId=12345", details, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildUpdateStepFailureDetails_IncludesFallbackStepContext()
    {
        var method = typeof(ActiveDirectoryCommandGateway).GetMethod("BuildUpdateStepFailureDetails", BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);

        var command = new DirectoryMutationCommand(
            Action: "UpdateUser",
            WorkerId: "00225",
            ManagerId: null,
            ManagerDistinguishedName: null,
            SamAccountName: "00225",
            CommonName: "00225",
            UserPrincipalName: "tanya.willislivers@example.com",
            Mail: "tanya.willislivers@example.com",
            TargetOu: "OU=Users,DC=example,DC=com",
            DisplayName: "Willis Livers, Tanya",
            CurrentDistinguishedName: null,
            EnableAccount: true,
            Operations: [new SyncFactors.Contracts.DirectoryOperation("UpdateUser")],
            Attributes: new Dictionary<string, string?>());

        var details = Assert.IsType<string>(method!.Invoke(null, [command, null, null, null, "FindExistingUser"]));

        Assert.Contains("Step=FindExistingUser", details, StringComparison.Ordinal);
        Assert.Contains("DistinguishedName=(unset)", details, StringComparison.Ordinal);
        Assert.Contains("CurrentCn=(unset)", details, StringComparison.Ordinal);
        Assert.Contains("Attributes=(none)", details, StringComparison.Ordinal);
    }

    [Fact]
    public void ResolveUserAccountControl_PreservesExistingDirectoryFlags()
    {
        var method = typeof(ActiveDirectoryCommandGateway).GetMethod("ResolveUserAccountControl", BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);

        var snapshot = new DirectoryUserSnapshot(
            SamAccountName: "00051",
            DistinguishedName: "CN=00051,OU=Users,DC=example,DC=com",
            Enabled: false,
            DisplayName: "Sample, User",
            Attributes: new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
            {
                ["userAccountControl"] = "66050"
            });

        var value = Assert.IsType<int>(method!.Invoke(null, [snapshot]));

        Assert.Equal(66050, value);
    }

    [Fact]
    public void GetSearchBases_IncludesLeaveOu()
    {
        var method = typeof(ActiveDirectoryCommandGateway).GetMethod("GetSearchBases", BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);

        var config = new ActiveDirectoryConfig(
            Server: "localhost",
            Port: 389,
            Username: "bind",
            BindPassword: "secret",
            IdentityAttribute: "employeeID",
            DefaultActiveOu: "OU=Active,DC=example,DC=com",
            PrehireOu: "OU=Prehire,DC=example,DC=com",
            GraveyardOu: "OU=Graveyard,DC=example,DC=com",
            Transport: new ActiveDirectoryTransportConfig("ldap", false, false, false, []),
            IdentityPolicy: new ActiveDirectoryIdentityPolicyConfig(false),
            LeaveOu: "OU=Leave,DC=example,DC=com");

        var searchBases = Assert.IsAssignableFrom<IReadOnlyList<string>>(method!.Invoke(null, [config]));

        Assert.Contains("OU=Leave,DC=example,DC=com", searchBases);
    }

    private static DirectoryAttributeModification CreateModification(string name)
    {
        return new DirectoryAttributeModification
        {
            Name = name,
            Operation = DirectoryAttributeOperation.Replace
        };
    }
}

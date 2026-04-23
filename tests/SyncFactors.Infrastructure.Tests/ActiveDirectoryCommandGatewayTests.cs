using System.DirectoryServices.Protocols;
using System.Reflection;
using Microsoft.Extensions.Logging.Abstractions;
using SyncFactors.Contracts;
using SyncFactors.Infrastructure;

namespace SyncFactors.Infrastructure.Tests;

public sealed class ActiveDirectoryCommandGatewayTests
{
    private static T ThrowCreateConflictLookupFailure<T>()
    {
        throw new DirectoryOperationException(
            "The object does not exist. 0000208D: NameErr: DSID-0310028D, problem 2001 (NO_OBJECT), data 0");
    }

    [Fact]
    public void GetParentDistinguishedName_IgnoresEscapedCommaInCommonName()
    {
        var method = typeof(ActiveDirectoryCommandGateway).GetMethod("GetParentDistinguishedName", BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);

        var parentDn = Assert.IsType<string>(method!.Invoke(null, ["CN=Brien\\, Christopher,OU=IT,DC=example,DC=com"]));

        Assert.Equal("OU=IT,DC=example,DC=com", parentDn);
    }

    [Fact]
    public void GetRelativeDistinguishedName_IgnoresEscapedCommaInCommonName()
    {
        var method = typeof(ActiveDirectoryCommandGateway).GetMethod("GetRelativeDistinguishedName", BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);

        var relativeDn = Assert.IsType<string>(method!.Invoke(null, ["CN=Brien\\, Christopher,OU=IT,DC=example,DC=com"]));

        Assert.Equal("CN=Brien\\, Christopher", relativeDn);
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
    public void BuildCreateFailureDetails_IncludesCreateContext()
    {
        var method = typeof(ActiveDirectoryCommandGateway).GetMethod("BuildCreateFailureDetails", BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);

        var command = new DirectoryMutationCommand(
            Action: "CreateUser",
            WorkerId: "45086",
            ManagerId: "90001",
            ManagerDistinguishedName: null,
            SamAccountName: "45086",
            CommonName: "45086",
            UserPrincipalName: "45086@example.com",
            Mail: "45086@example.com",
            TargetOu: "OU=Users,DC=example,DC=com",
            DisplayName: "Worker, Example",
            CurrentDistinguishedName: null,
            EnableAccount: true,
            Operations: [new SyncFactors.Contracts.DirectoryOperation("CreateUser")],
            Attributes: new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
            {
                ["employeeID"] = "45086"
            });
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
            LicensingGroups: ["CN=M365-E3-Prestage,OU=Groups,DC=example,DC=com"]);
        var attributes = new List<DirectoryAttribute>
        {
            new("sAMAccountName", "45086"),
            new("userPrincipalName", "45086@example.com"),
            new("mail", "45086@example.com"),
            new("employeeID", "45086")
        };

        var details = Assert.IsType<string>(method!.Invoke(null, [command, "CN=45086,OU=Users,DC=example,DC=com", config, attributes, "CreateUser", null, null]));

        Assert.Contains("Step=CreateUser", details, StringComparison.Ordinal);
        Assert.Contains("TargetOu=OU=Users,DC=example,DC=com", details, StringComparison.Ordinal);
        Assert.Contains("UserPrincipalName=45086@example.com", details, StringComparison.Ordinal);
        Assert.Contains("Mail=45086@example.com", details, StringComparison.Ordinal);
        Assert.Contains("IdentityAttribute=employeeID", details, StringComparison.Ordinal);
        Assert.Contains("IdentityValue=45086", details, StringComparison.Ordinal);
        Assert.Contains("CreateAttributes=sAMAccountName,userPrincipalName,mail,employeeID", details, StringComparison.Ordinal);
        Assert.Contains("LicensingGroups=CN=M365-E3-Prestage,OU=Groups,DC=example,DC=com", details, StringComparison.Ordinal);
        Assert.Contains("ExistingSamAccountName=(unset)", details, StringComparison.Ordinal);
        Assert.Contains("ExistingDisplayName=(unset)", details, StringComparison.Ordinal);
        Assert.Contains("ExistingDistinguishedName=(unset)", details, StringComparison.Ordinal);
        Assert.Contains("ManagerId=90001", details, StringComparison.Ordinal);
        Assert.Contains("ManagerDistinguishedName=(unset)", details, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildCreateFailureDetails_IncludesExistingAccountContext()
    {
        var method = typeof(ActiveDirectoryCommandGateway).GetMethod("BuildCreateFailureDetails", BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);

        var command = new DirectoryMutationCommand(
            Action: "CreateUser",
            WorkerId: "30008382",
            ManagerId: "38256",
            ManagerDistinguishedName: null,
            SamAccountName: "30008382",
            CommonName: "30008382",
            UserPrincipalName: "david.ramsey@example.com",
            Mail: "david.ramsey@example.com",
            TargetOu: "OU=POWERSHELL,OU=ExampleQA-Users,DC=ExampleQA,DC=biz",
            DisplayName: "Ramsey, David",
            CurrentDistinguishedName: null,
            EnableAccount: true,
            Operations: [new SyncFactors.Contracts.DirectoryOperation("CreateUser")],
            Attributes: new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
            {
                ["sAMAccountName"] = "30008382"
            });
        var config = new ActiveDirectoryConfig(
            Server: "localhost",
            Port: 636,
            Username: "bind",
            BindPassword: "secret",
            IdentityAttribute: "sAMAccountName",
            DefaultActiveOu: "OU=POWERSHELL,OU=ExampleQA-Users,DC=ExampleQA,DC=biz",
            PrehireOu: "OU=Prehire,OU=ExampleQA-Users,DC=ExampleQA,DC=biz",
            GraveyardOu: "OU=GRAVEYARD,OU=ExampleQA-Users,DC=ExampleQA,DC=biz",
            Transport: new ActiveDirectoryTransportConfig("ldaps", false, true, true, []),
            IdentityPolicy: new ActiveDirectoryIdentityPolicyConfig(false));
        var attributes = new List<DirectoryAttribute>
        {
            new("sAMAccountName", "30008382"),
            new("userPrincipalName", "david.ramsey@example.com"),
            new("mail", "david.ramsey@example.com")
        };
        var existingAccountType = typeof(ActiveDirectoryCommandGateway).GetNestedType("ExistingAccountDetails", BindingFlags.NonPublic);
        Assert.NotNull(existingAccountType);
        var existingAccount = Activator.CreateInstance(
            existingAccountType!,
            "30008382",
            "Ramsey, David",
            "CN=30008382,OU=POWERSHELL,OU=ExampleQA-Users,DC=ExampleQA,DC=biz",
            "david.ramsey@example.com",
            "david.ramsey@example.com",
            new[] { "top", "person", "organizationalPerson", "user" },
            "Found",
            null,
            "NotAttempted",
            null,
            "Completed",
            null,
            new[] { "CN=30008382,OU=POWERSHELL,OU=ExampleQA-Users,DC=ExampleQA,DC=biz" },
            new[] { "CN=30008382,OU=POWERSHELL,OU=ExampleQA-Users,DC=ExampleQA,DC=biz" },
            new[] { "CN=30008382,OU=POWERSHELL,OU=ExampleQA-Users,DC=ExampleQA,DC=biz" },
            new[] { "CN=30008382,OU=POWERSHELL,OU=ExampleQA-Users,DC=ExampleQA,DC=biz" },
            new[] { "CN=30008382,OU=POWERSHELL,OU=ExampleQA-Users,DC=ExampleQA,DC=biz" });

        var details = Assert.IsType<string>(method!.Invoke(null, [command, "CN=30008382,OU=POWERSHELL,OU=ExampleQA-Users,DC=ExampleQA,DC=biz", config, attributes, "CreateUser", null, existingAccount!]));

        Assert.Contains("ExactDnLookupOutcome=Found", details, StringComparison.Ordinal);
        Assert.Contains("ExistingObjectClasses=top;person;organizationalPerson;user", details, StringComparison.Ordinal);
        Assert.Contains("ExistingSamAccountName=30008382", details, StringComparison.Ordinal);
        Assert.Contains("ExistingDisplayName=Ramsey, David", details, StringComparison.Ordinal);
        Assert.Contains("ExistingDistinguishedName=CN=30008382,OU=POWERSHELL,OU=ExampleQA-Users,DC=ExampleQA,DC=biz", details, StringComparison.Ordinal);
        Assert.Contains("ExistingUserPrincipalName=david.ramsey@example.com", details, StringComparison.Ordinal);
        Assert.Contains("ExistingMail=david.ramsey@example.com", details, StringComparison.Ordinal);
        Assert.Contains("IdentityConflictLookupOutcome=NotAttempted", details, StringComparison.Ordinal);
        Assert.Contains("DomainCollisionLookupOutcome=Completed", details, StringComparison.Ordinal);
        Assert.Contains("DomainCnMatches=CN=30008382,OU=POWERSHELL,OU=ExampleQA-Users,DC=ExampleQA,DC=biz", details, StringComparison.Ordinal);
        Assert.Contains("DomainNameMatches=CN=30008382,OU=POWERSHELL,OU=ExampleQA-Users,DC=ExampleQA,DC=biz", details, StringComparison.Ordinal);
        Assert.Contains("DomainSamAccountNameMatches=CN=30008382,OU=POWERSHELL,OU=ExampleQA-Users,DC=ExampleQA,DC=biz", details, StringComparison.Ordinal);
        Assert.Contains("DomainUserPrincipalNameMatches=CN=30008382,OU=POWERSHELL,OU=ExampleQA-Users,DC=ExampleQA,DC=biz", details, StringComparison.Ordinal);
        Assert.Contains("DomainMailMatches=CN=30008382,OU=POWERSHELL,OU=ExampleQA-Users,DC=ExampleQA,DC=biz", details, StringComparison.Ordinal);
    }

    [Fact]
    public void TryBuildOuterCatchFailureDetails_ForCreateUser_IncludesDistinguishedNameAndTargetOu()
    {
        var method = typeof(ActiveDirectoryCommandGateway).GetMethod("TryBuildOuterCatchFailureDetails", BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);

        var command = new DirectoryMutationCommand(
            Action: "CreateUser",
            WorkerId: "45511",
            ManagerId: null,
            ManagerDistinguishedName: null,
            SamAccountName: "45511",
            CommonName: "45511",
            UserPrincipalName: "kimberly.turner@example.com",
            Mail: "kimberly.turner@example.com",
            TargetOu: "OU=POWERSHELL,OU=ExampleQA-Users,DC=ExampleQA,DC=biz",
            DisplayName: "Turner, Kimberly",
            CurrentDistinguishedName: null,
            EnableAccount: true,
            Operations: [new SyncFactors.Contracts.DirectoryOperation("CreateUser")],
            Attributes: new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
            {
                ["sAMAccountName"] = "45511"
            });
        var config = new ActiveDirectoryConfig(
            Server: "localhost",
            Port: 636,
            Username: "bind",
            BindPassword: "secret",
            IdentityAttribute: "sAMAccountName",
            DefaultActiveOu: "OU=POWERSHELL,OU=ExampleQA-Users,DC=ExampleQA,DC=biz",
            PrehireOu: "OU=Prehire,OU=ExampleQA-Users,DC=ExampleQA,DC=biz",
            GraveyardOu: "OU=GRAVEYARD,OU=ExampleQA-Users,DC=ExampleQA,DC=biz",
            Transport: new ActiveDirectoryTransportConfig("ldaps", false, true, true, []),
            IdentityPolicy: new ActiveDirectoryIdentityPolicyConfig(false));

        var details = Assert.IsType<string>(method!.Invoke(null, [command, config]));

        Assert.Contains("Step=ExecuteAsyncOuterCatch", details, StringComparison.Ordinal);
        Assert.Contains("DistinguishedName=CN=45511,OU=POWERSHELL,OU=ExampleQA-Users,DC=ExampleQA,DC=biz", details, StringComparison.Ordinal);
        Assert.Contains("TargetOu=OU=POWERSHELL,OU=ExampleQA-Users,DC=ExampleQA,DC=biz", details, StringComparison.Ordinal);
        Assert.Contains("UserPrincipalName=kimberly.turner@example.com", details, StringComparison.Ordinal);
        Assert.Contains("Mail=kimberly.turner@example.com", details, StringComparison.Ordinal);
        Assert.Contains("CreateAttributes=objectClass,cn,displayName,sAMAccountName,userPrincipalName,mail,userAccountControl", details, StringComparison.Ordinal);
    }

    [Fact]
    public void TryAugmentCreateFailureDetailsWithExistingAccountConflict_FallsBackWhenConflictLookupThrows()
    {
        var method = typeof(ActiveDirectoryCommandGateway).GetMethod("TryAugmentCreateFailureDetailsWithExistingAccountConflict", BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);

        var command = new DirectoryMutationCommand(
            Action: "CreateUser",
            WorkerId: "45086",
            ManagerId: "43114",
            ManagerDistinguishedName: "CN=43114,OU=POWERSHELL,OU=ExampleQA-Users,DC=ExampleQA,DC=biz",
            SamAccountName: "45086",
            CommonName: "45086",
            UserPrincipalName: "brian.oliver@example.com",
            Mail: "brian.oliver@example.com",
            TargetOu: "OU=POWERSHELL,OU=ExampleQA-Users,DC=ExampleQA,DC=biz",
            DisplayName: "Oliver, Brian",
            CurrentDistinguishedName: null,
            EnableAccount: true,
            Operations: [new SyncFactors.Contracts.DirectoryOperation("CreateUser")],
            Attributes: new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
            {
                ["sAMAccountName"] = "45086"
            });
        var config = new ActiveDirectoryConfig(
            Server: "example-env-01.Exampleqa.biz",
            Port: 636,
            Username: "bind",
            BindPassword: "secret",
            IdentityAttribute: "sAMAccountName",
            DefaultActiveOu: "OU=POWERSHELL,OU=ExampleQA-Users,DC=ExampleQA,DC=biz",
            PrehireOu: "OU=Prehire,OU=ExampleQA-Users,DC=ExampleQA,DC=biz",
            GraveyardOu: "OU=GRAVEYARD,OU=ExampleQA-Users,DC=ExampleQA,DC=biz",
            Transport: new ActiveDirectoryTransportConfig("ldaps", false, true, true, []),
            IdentityPolicy: new ActiveDirectoryIdentityPolicyConfig(false));
        var attributes = new List<DirectoryAttribute>
        {
            new("objectClass", "top", "person", "organizationalPerson", "user"),
            new("cn", "45086"),
            new("displayName", "Oliver, Brian"),
            new("sAMAccountName", "45086"),
            new("userPrincipalName", "brian.oliver@example.com"),
            new("mail", "brian.oliver@example.com"),
            new("userAccountControl", "514")
        };
        var fallbackDetails =
            "Step=CreateUserAddRequest WorkerId=45086 SamAccountName=45086 DistinguishedName=CN=45086,OU=POWERSHELL,OU=ExampleQA-Users,DC=ExampleQA,DC=biz " +
            "TargetOu=OU=POWERSHELL,OU=ExampleQA-Users,DC=ExampleQA,DC=biz UserPrincipalName=brian.oliver@example.com Mail=brian.oliver@example.com";
        var existingAccountType = typeof(ActiveDirectoryCommandGateway).GetNestedType("ExistingAccountDetails", BindingFlags.NonPublic);
        Assert.NotNull(existingAccountType);
        var delegateType = typeof(Func<>).MakeGenericType(existingAccountType!);
        var throwMethod = typeof(ActiveDirectoryCommandGatewayTests).GetMethod(nameof(ThrowCreateConflictLookupFailure), BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(throwMethod);
        var resolveConflict = Delegate.CreateDelegate(delegateType, throwMethod!.MakeGenericMethod(existingAccountType!));

        var details = Assert.IsType<string>(method!.Invoke(null, [resolveConflict, command, "CN=45086,OU=POWERSHELL,OU=ExampleQA-Users,DC=ExampleQA,DC=biz", config, attributes, "CreateUserAddRequest", command.ManagerDistinguishedName, NullLogger<ActiveDirectoryCommandGateway>.Instance, fallbackDetails]));

        Assert.Contains("Step=CreateUserAddRequest", details, StringComparison.Ordinal);
        Assert.Contains("DistinguishedName=CN=45086,OU=POWERSHELL,OU=ExampleQA-Users,DC=ExampleQA,DC=biz", details, StringComparison.Ordinal);
        Assert.Contains("ConflictResolutionLookupFailed=true", details, StringComparison.Ordinal);
        Assert.Contains("ConflictResolutionLookupError=The object does not exist. 0000208D: NameErr: DSID-0310028D, problem 2001 (NO_OBJECT), data 0", details, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildProvisioningGroupRequests_UsesDistinctTrimmedConfiguredGroups()
    {
        var method = typeof(ActiveDirectoryCommandGateway).GetMethod("BuildProvisioningGroupRequests", BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);

        var config = new ActiveDirectoryConfig(
            Server: "localhost",
            Port: 636,
            Username: "bind",
            BindPassword: "secret",
            IdentityAttribute: "employeeID",
            DefaultActiveOu: "OU=Active,DC=example,DC=com",
            PrehireOu: "OU=Prehire,DC=example,DC=com",
            GraveyardOu: "OU=Graveyard,DC=example,DC=com",
            Transport: new ActiveDirectoryTransportConfig("ldaps", false, true, true, []),
            IdentityPolicy: new ActiveDirectoryIdentityPolicyConfig(false),
            LicensingGroups:
            [
                " CN=M365-E3-Prestage,OU=Groups,DC=example,DC=com ",
                "CN=VPN-Users,OU=Groups,DC=example,DC=com",
                "CN=M365-E3-Prestage,OU=Groups,DC=example,DC=com"
            ]);

        var requests = Assert.IsAssignableFrom<IReadOnlyList<ModifyRequest>>(method!.Invoke(null, ["CN=45086,OU=Users,DC=example,DC=com", config]));

        Assert.Equal(2, requests.Count);
        Assert.Equal("CN=M365-E3-Prestage,OU=Groups,DC=example,DC=com", requests[0].DistinguishedName);
        Assert.Equal("CN=VPN-Users,OU=Groups,DC=example,DC=com", requests[1].DistinguishedName);

        var modification = Assert.Single(requests[0].Modifications.Cast<DirectoryAttributeModification>());
        Assert.Equal("member", modification.Name);
        Assert.Equal(DirectoryAttributeOperation.Add, modification.Operation);
        Assert.Equal("CN=45086,OU=Users,DC=example,DC=com", modification[0]?.ToString());
    }

    [Fact]
    public void BuildProvisioningGroupRemovalRequests_UsesDeleteMemberModifications()
    {
        var method = typeof(ActiveDirectoryCommandGateway).GetMethod("BuildProvisioningGroupRemovalRequests", BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);

        var config = new ActiveDirectoryConfig(
            Server: "localhost",
            Port: 636,
            Username: "bind",
            BindPassword: "secret",
            IdentityAttribute: "employeeID",
            DefaultActiveOu: "OU=Active,DC=example,DC=com",
            PrehireOu: "OU=Prehire,DC=example,DC=com",
            GraveyardOu: "OU=Graveyard,DC=example,DC=com",
            Transport: new ActiveDirectoryTransportConfig("ldaps", false, true, true, []),
            IdentityPolicy: new ActiveDirectoryIdentityPolicyConfig(false),
            LicensingGroups:
            [
                "CN=M365-E3-Prestage,OU=Groups,DC=example,DC=com"
            ]);

        var requests = Assert.IsAssignableFrom<IReadOnlyList<ModifyRequest>>(method!.Invoke(null, ["CN=45086,OU=Users,DC=example,DC=com", config]));

        var modification = Assert.Single(requests.Single().Modifications.Cast<DirectoryAttributeModification>());
        Assert.Equal("member", modification.Name);
        Assert.Equal(DirectoryAttributeOperation.Delete, modification.Operation);
        Assert.Equal("CN=45086,OU=Users,DC=example,DC=com", modification[0]?.ToString());
    }

    [Fact]
    public void ShouldRemoveProvisioningGroups_ReturnsTrueForDisabledGraveyardUsers()
    {
        var method = typeof(ActiveDirectoryCommandGateway).GetMethod("ShouldRemoveProvisioningGroups", BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);

        var command = new DirectoryMutationCommand(
            Action: "DisableUser",
            WorkerId: "45086",
            ManagerId: null,
            ManagerDistinguishedName: null,
            SamAccountName: "45086",
            CommonName: "45086",
            UserPrincipalName: "45086@example.com",
            Mail: "45086@example.com",
            TargetOu: "OU=Graveyard,DC=example,DC=com",
            DisplayName: "Worker, Example",
            CurrentDistinguishedName: "CN=45086,OU=Employees,DC=example,DC=com",
            EnableAccount: false,
            Operations: [new SyncFactors.Contracts.DirectoryOperation("DisableUser")],
            Attributes: new Dictionary<string, string?>());
        var config = new ActiveDirectoryConfig(
            Server: "localhost",
            Port: 636,
            Username: "bind",
            BindPassword: "secret",
            IdentityAttribute: "employeeID",
            DefaultActiveOu: "OU=Active,DC=example,DC=com",
            PrehireOu: "OU=Prehire,DC=example,DC=com",
            GraveyardOu: "OU=Graveyard,DC=example,DC=com",
            Transport: new ActiveDirectoryTransportConfig("ldaps", false, true, true, []),
            IdentityPolicy: new ActiveDirectoryIdentityPolicyConfig(false),
            LicensingGroups: ["CN=M365-E3-Prestage,OU=Groups,DC=example,DC=com"]);

        var shouldRemove = Assert.IsType<bool>(method!.Invoke(null, [command, config, "CN=45086,OU=Graveyard,DC=example,DC=com"]));

        Assert.True(shouldRemove);
    }

    [Fact]
    public void ShouldRemoveProvisioningGroups_ReturnsFalseForLeaveUsers()
    {
        var method = typeof(ActiveDirectoryCommandGateway).GetMethod("ShouldRemoveProvisioningGroups", BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);

        var command = new DirectoryMutationCommand(
            Action: "DisableUser",
            WorkerId: "45086",
            ManagerId: null,
            ManagerDistinguishedName: null,
            SamAccountName: "45086",
            CommonName: "45086",
            UserPrincipalName: "45086@example.com",
            Mail: "45086@example.com",
            TargetOu: "OU=Leave,DC=example,DC=com",
            DisplayName: "Worker, Example",
            CurrentDistinguishedName: "CN=45086,OU=Employees,DC=example,DC=com",
            EnableAccount: false,
            Operations: [new SyncFactors.Contracts.DirectoryOperation("DisableUser")],
            Attributes: new Dictionary<string, string?>());
        var config = new ActiveDirectoryConfig(
            Server: "localhost",
            Port: 636,
            Username: "bind",
            BindPassword: "secret",
            IdentityAttribute: "employeeID",
            DefaultActiveOu: "OU=Active,DC=example,DC=com",
            PrehireOu: "OU=Prehire,DC=example,DC=com",
            GraveyardOu: "OU=Graveyard,DC=example,DC=com",
            Transport: new ActiveDirectoryTransportConfig("ldaps", false, true, true, []),
            IdentityPolicy: new ActiveDirectoryIdentityPolicyConfig(false),
            LicensingGroups: ["CN=M365-E3-Prestage,OU=Groups,DC=example,DC=com"]);

        var shouldRemove = Assert.IsType<bool>(method!.Invoke(null, [command, config, "CN=45086,OU=Leave,DC=example,DC=com"]));

        Assert.False(shouldRemove);
    }

    [Fact]
    public void ShouldVerifyGraveyardMove_ReturnsTrueForMoveIntoConfiguredGraveyardOu()
    {
        var method = typeof(ActiveDirectoryCommandGateway).GetMethod("ShouldVerifyGraveyardMove", BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);

        var command = new DirectoryMutationCommand(
            Action: "MoveUser",
            WorkerId: "45086",
            ManagerId: null,
            ManagerDistinguishedName: null,
            SamAccountName: "45086",
            CommonName: "45086",
            UserPrincipalName: "45086@example.com",
            Mail: "45086@example.com",
            TargetOu: "OU=Graveyard,DC=example,DC=com",
            DisplayName: "Worker, Example",
            CurrentDistinguishedName: "CN=45086,OU=Employees,DC=example,DC=com",
            EnableAccount: false,
            Operations:
            [
                new SyncFactors.Contracts.DirectoryOperation("MoveUser", "OU=Graveyard,DC=example,DC=com"),
                new SyncFactors.Contracts.DirectoryOperation("DisableUser")
            ],
            Attributes: new Dictionary<string, string?>());
        var config = new ActiveDirectoryConfig(
            Server: "localhost",
            Port: 636,
            Username: "bind",
            BindPassword: "secret",
            IdentityAttribute: "employeeID",
            DefaultActiveOu: "OU=Active,DC=example,DC=com",
            PrehireOu: "OU=Prehire,DC=example,DC=com",
            GraveyardOu: "OU=Graveyard,DC=example,DC=com",
            Transport: new ActiveDirectoryTransportConfig("ldaps", false, true, true, []),
            IdentityPolicy: new ActiveDirectoryIdentityPolicyConfig(false));

        var shouldVerify = Assert.IsType<bool>(method!.Invoke(null, [command, command.Operations, config]));

        Assert.True(shouldVerify);
    }

    [Fact]
    public void ShouldVerifyGraveyardMove_ReturnsFalseWhenNoMoveIsPlanned()
    {
        var method = typeof(ActiveDirectoryCommandGateway).GetMethod("ShouldVerifyGraveyardMove", BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);

        var command = new DirectoryMutationCommand(
            Action: "DisableUser",
            WorkerId: "45086",
            ManagerId: null,
            ManagerDistinguishedName: null,
            SamAccountName: "45086",
            CommonName: "45086",
            UserPrincipalName: "45086@example.com",
            Mail: "45086@example.com",
            TargetOu: "OU=Graveyard,DC=example,DC=com",
            DisplayName: "Worker, Example",
            CurrentDistinguishedName: "CN=45086,OU=Graveyard,DC=example,DC=com",
            EnableAccount: false,
            Operations:
            [
                new SyncFactors.Contracts.DirectoryOperation("UpdateUser"),
                new SyncFactors.Contracts.DirectoryOperation("DisableUser")
            ],
            Attributes: new Dictionary<string, string?>());
        var config = new ActiveDirectoryConfig(
            Server: "localhost",
            Port: 636,
            Username: "bind",
            BindPassword: "secret",
            IdentityAttribute: "employeeID",
            DefaultActiveOu: "OU=Active,DC=example,DC=com",
            PrehireOu: "OU=Prehire,DC=example,DC=com",
            GraveyardOu: "OU=Graveyard,DC=example,DC=com",
            Transport: new ActiveDirectoryTransportConfig("ldaps", false, true, true, []),
            IdentityPolicy: new ActiveDirectoryIdentityPolicyConfig(false));

        var shouldVerify = Assert.IsType<bool>(method!.Invoke(null, [command, command.Operations, config]));

        Assert.False(shouldVerify);
    }

    [Fact]
    public void BuildGraveyardVerificationFailureDetails_IncludesExpectedAndActualState()
    {
        var method = typeof(ActiveDirectoryCommandGateway).GetMethod("BuildGraveyardVerificationFailureDetails", BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);

        var command = new DirectoryMutationCommand(
            Action: "MoveUser",
            WorkerId: "45086",
            ManagerId: null,
            ManagerDistinguishedName: null,
            SamAccountName: "45086",
            CommonName: "45086",
            UserPrincipalName: "45086@example.com",
            Mail: "45086@example.com",
            TargetOu: "OU=Graveyard,DC=example,DC=com",
            DisplayName: "Worker, Example",
            CurrentDistinguishedName: "CN=45086,OU=Employees,DC=example,DC=com",
            EnableAccount: false,
            Operations:
            [
                new SyncFactors.Contracts.DirectoryOperation("MoveUser", "OU=Graveyard,DC=example,DC=com"),
                new SyncFactors.Contracts.DirectoryOperation("DisableUser")
            ],
            Attributes: new Dictionary<string, string?>());
        var config = new ActiveDirectoryConfig(
            Server: "localhost",
            Port: 636,
            Username: "bind",
            BindPassword: "secret",
            IdentityAttribute: "employeeID",
            DefaultActiveOu: "OU=Active,DC=example,DC=com",
            PrehireOu: "OU=Prehire,DC=example,DC=com",
            GraveyardOu: "OU=Graveyard,DC=example,DC=com",
            Transport: new ActiveDirectoryTransportConfig("ldaps", false, true, true, []),
            IdentityPolicy: new ActiveDirectoryIdentityPolicyConfig(false));
        var actual = new DirectoryUserSnapshot(
            SamAccountName: "45086",
            DistinguishedName: "CN=45086,OU=Employees,DC=example,DC=com",
            Enabled: true,
            DisplayName: "Worker, Example",
            Attributes: new Dictionary<string, string?>());

        var details = Assert.IsType<string>(method!.Invoke(null, [command, config, "45086", "CN=45086,OU=Graveyard,DC=example,DC=com", actual]));

        Assert.Contains("Step=VerifyGraveyardMove", details, StringComparison.Ordinal);
        Assert.Contains("IdentityAttribute=employeeID", details, StringComparison.Ordinal);
        Assert.Contains("IdentityLookupValue=45086", details, StringComparison.Ordinal);
        Assert.Contains("ExpectedParentOu=OU=Graveyard,DC=example,DC=com", details, StringComparison.Ordinal);
        Assert.Contains("ActualDistinguishedName=CN=45086,OU=Employees,DC=example,DC=com", details, StringComparison.Ordinal);
        Assert.Contains("ActualParentOu=OU=Employees,DC=example,DC=com", details, StringComparison.Ordinal);
        Assert.Contains("ActualEnabled=true", details, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildIdentityConflictDetails_IncludesExistingAccountContext()
    {
        var method = typeof(ActiveDirectoryCommandGateway).GetMethod("BuildIdentityConflictDetails", BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);

        var command = new DirectoryMutationCommand(
            Action: "CreateUser",
            WorkerId: "45086",
            ManagerId: "43114",
            ManagerDistinguishedName: "CN=43114,OU=Users,DC=example,DC=com",
            SamAccountName: "45086",
            CommonName: "45086",
            UserPrincipalName: "brian.oliver@Exampleenergy.com",
            Mail: "brian.oliver@Exampleenergy.com",
            TargetOu: "OU=Users,DC=example,DC=com",
            DisplayName: "Oliver, Brian",
            CurrentDistinguishedName: null,
            EnableAccount: true,
            Operations: [new SyncFactors.Contracts.DirectoryOperation("CreateUser")],
            Attributes: new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase));
        var config = new ActiveDirectoryConfig(
            Server: "192.0.2.10",
            Port: 389,
            Username: "bind",
            BindPassword: "secret",
            IdentityAttribute: "sAMAccountName",
            DefaultActiveOu: "OU=Active,DC=example,DC=com",
            PrehireOu: "OU=Prehire,DC=example,DC=com",
            GraveyardOu: "OU=Graveyard,DC=example,DC=com",
            Transport: new ActiveDirectoryTransportConfig("ldap", false, false, false, []),
            IdentityPolicy: new ActiveDirectoryIdentityPolicyConfig(false));
        var conflictType = typeof(ActiveDirectoryCommandGateway).GetNestedType("IdentityConflictResult", BindingFlags.NonPublic);
        Assert.NotNull(conflictType);
        var conflict = Activator.CreateInstance(
            conflictType!,
            "userPrincipalName",
            "brian.oliver@Exampleenergy.com",
            "boliver",
            "Oliver, Brian",
            "CN=Brian Oliver,OU=Existing,DC=example,DC=com",
            "brian.oliver@Exampleenergy.com",
            "brian.oliver@Exampleenergy.com");

        var details = Assert.IsType<string>(method!.Invoke(null, [command, "CN=45086,OU=Users,DC=example,DC=com", config, conflict!]));

        Assert.Contains("Step=PreflightIdentityConflict", details, StringComparison.Ordinal);
        Assert.Contains("ConflictingAttribute=userPrincipalName", details, StringComparison.Ordinal);
        Assert.Contains("ConflictingValue=brian.oliver@Exampleenergy.com", details, StringComparison.Ordinal);
        Assert.Contains("ExistingSamAccountName=boliver", details, StringComparison.Ordinal);
        Assert.Contains("ExistingDisplayName=Oliver, Brian", details, StringComparison.Ordinal);
        Assert.Contains("ExistingDistinguishedName=CN=Brian Oliver,OU=Existing,DC=example,DC=com", details, StringComparison.Ordinal);
        Assert.Contains("ExistingUserPrincipalName=brian.oliver@Exampleenergy.com", details, StringComparison.Ordinal);
        Assert.Contains("ExistingMail=brian.oliver@Exampleenergy.com", details, StringComparison.Ordinal);
        Assert.Contains("IdentityAttribute=sAMAccountName", details, StringComparison.Ordinal);
        Assert.Contains("IdentityValue=45086", details, StringComparison.Ordinal);
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
    public void ResolveTargetDistinguishedName_PrefersCommandDistinguishedName()
    {
        var method = typeof(ActiveDirectoryCommandGateway).GetMethod("ResolveTargetDistinguishedName", BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);

        var existing = new DirectoryUserSnapshot(
            SamAccountName: "00051",
            DistinguishedName: "CN=00051,OU=Users,DC=example,DC=com",
            Enabled: true,
            DisplayName: "Sample, User",
            Attributes: new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase));

        var resolved = Assert.IsType<string>(method!.Invoke(null, ["CN=Current,OU=Users,DC=example,DC=com", existing]));

        Assert.Equal("CN=Current,OU=Users,DC=example,DC=com", resolved);
    }

    [Fact]
    public void ResolveCurrentCommonName_FallsBackToDistinguishedName()
    {
        var method = typeof(ActiveDirectoryCommandGateway).GetMethod("ResolveCurrentCommonName", BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);

        var existing = new DirectoryUserSnapshot(
            SamAccountName: "00051",
            DistinguishedName: "CN=00051,OU=Users,DC=example,DC=com",
            Enabled: true,
            DisplayName: "Sample, User",
            Attributes: new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase));

        var resolved = Assert.IsType<string>(method!.Invoke(null, ["CN=Brien\\, Christopher,OU=IT,DC=example,DC=com", existing]));

        Assert.Equal("Brien\\, Christopher", resolved);
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

    [Fact]
    public void BuildCreateAttributes_PrefersMappedIdentityAttributeValue()
    {
        var method = typeof(ActiveDirectoryCommandGateway).GetMethod("BuildCreateAttributes", BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);

        var command = new DirectoryMutationCommand(
            Action: "CreateUser",
            WorkerId: "10001",
            ManagerId: null,
            ManagerDistinguishedName: null,
            SamAccountName: "user.10001",
            CommonName: "user.10001",
            UserPrincipalName: "user.10001@example.com",
            Mail: "user.10001@example.com",
            TargetOu: "OU=Users,DC=example,DC=com",
            DisplayName: "Sample, User",
            CurrentDistinguishedName: null,
            EnableAccount: true,
            Operations: [new SyncFactors.Contracts.DirectoryOperation("CreateUser")],
            Attributes: new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
            {
                ["employeeID"] = "20002",
                ["department"] = "IT"
            });
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
            IdentityPolicy: new ActiveDirectoryIdentityPolicyConfig(false));

        var attributes = Assert.IsAssignableFrom<IReadOnlyList<DirectoryAttribute>>(method!.Invoke(null, [command, config]));

        var employeeIdAttributes = attributes.Where(attribute => string.Equals(attribute.Name, "employeeID", StringComparison.OrdinalIgnoreCase)).ToArray();
        Assert.Single(employeeIdAttributes);
        Assert.Equal("20002", employeeIdAttributes[0].GetValues(typeof(string)).Cast<string>().Single());
        Assert.Equal(
            "514",
            attributes.Single(attribute => string.Equals(attribute.Name, "userAccountControl", StringComparison.OrdinalIgnoreCase))
                .GetValues(typeof(string))
                .Cast<string>()
                .Single());
    }

    [Fact]
    public void SupportsPasswordProvisioningTransport_ReturnsFalseForPlainLdap()
    {
        var method = typeof(ActiveDirectoryCommandGateway).GetMethod("SupportsPasswordProvisioningTransport", BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);

        var supported = Assert.IsType<bool>(method!.Invoke(null, ["ldap"]));

        Assert.False(supported);
    }

    [Theory]
    [InlineData("ldaps")]
    [InlineData("starttls")]
    public void SupportsPasswordProvisioningTransport_ReturnsTrueForSecureTransports(string effectiveTransport)
    {
        var method = typeof(ActiveDirectoryCommandGateway).GetMethod("SupportsPasswordProvisioningTransport", BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);

        var supported = Assert.IsType<bool>(method!.Invoke(null, [effectiveTransport]));

        Assert.True(supported);
    }

    [Fact]
    public void CanEnableCreatedAccount_ReturnsFalseForPlainLdapByDefault()
    {
        var method = typeof(ActiveDirectoryCommandGateway).GetMethod("CanEnableCreatedAccount", BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);

        var command = new DirectoryMutationCommand(
            Action: "CreateUser",
            WorkerId: "10001",
            ManagerId: null,
            ManagerDistinguishedName: null,
            SamAccountName: "user.10001",
            CommonName: "user.10001",
            UserPrincipalName: "user.10001@example.com",
            Mail: "user.10001@example.com",
            TargetOu: "OU=Users,DC=example,DC=com",
            DisplayName: "Sample, User",
            CurrentDistinguishedName: null,
            EnableAccount: true,
            Operations: [new SyncFactors.Contracts.DirectoryOperation("CreateUser")],
            Attributes: new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase));
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
            IdentityPolicy: new ActiveDirectoryIdentityPolicyConfig(false));

        var allowed = Assert.IsType<bool>(method!.Invoke(null, [command, config, "ldap"]));

        Assert.False(allowed);
    }

    [Fact]
    public void CanEnableCreatedAccount_ReturnsTrueForPlainLdapWhenFlagEnabled()
    {
        var method = typeof(ActiveDirectoryCommandGateway).GetMethod("CanEnableCreatedAccount", BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);

        var command = new DirectoryMutationCommand(
            Action: "CreateUser",
            WorkerId: "10001",
            ManagerId: null,
            ManagerDistinguishedName: null,
            SamAccountName: "user.10001",
            CommonName: "user.10001",
            UserPrincipalName: "user.10001@example.com",
            Mail: "user.10001@example.com",
            TargetOu: "OU=Users,DC=example,DC=com",
            DisplayName: "Sample, User",
            CurrentDistinguishedName: null,
            EnableAccount: true,
            Operations: [new SyncFactors.Contracts.DirectoryOperation("CreateUser")],
            Attributes: new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase));
        var config = new ActiveDirectoryConfig(
            Server: "localhost",
            Port: 389,
            Username: "bind",
            BindPassword: "secret",
            IdentityAttribute: "employeeID",
            DefaultActiveOu: "OU=Active,DC=example,DC=com",
            PrehireOu: "OU=Prehire,DC=example,DC=com",
            GraveyardOu: "OU=Graveyard,DC=example,DC=com",
            Transport: new ActiveDirectoryTransportConfig("ldap", false, false, false, [], true),
            IdentityPolicy: new ActiveDirectoryIdentityPolicyConfig(false));

        var allowed = Assert.IsType<bool>(method!.Invoke(null, [command, config, "ldap"]));

        Assert.True(allowed);
    }

    [Fact]
    public void BuildCreateCompletionMessage_ReportsEnabledAccountWhenPlainLdapFlagIsEnabled()
    {
        var method = typeof(ActiveDirectoryCommandGateway).GetMethod("BuildCreateCompletionMessage", BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);

        var command = new DirectoryMutationCommand(
            Action: "CreateUser",
            WorkerId: "10001",
            ManagerId: null,
            ManagerDistinguishedName: null,
            SamAccountName: "user.10001",
            CommonName: "user.10001",
            UserPrincipalName: "user.10001@example.com",
            Mail: "user.10001@example.com",
            TargetOu: "OU=Users,DC=example,DC=com",
            DisplayName: "Sample, User",
            CurrentDistinguishedName: null,
            EnableAccount: true,
            Operations: [new SyncFactors.Contracts.DirectoryOperation("CreateUser")],
            Attributes: new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase));
        var config = new ActiveDirectoryConfig(
            Server: "localhost",
            Port: 389,
            Username: "bind",
            BindPassword: "secret",
            IdentityAttribute: "employeeID",
            DefaultActiveOu: "OU=Active,DC=example,DC=com",
            PrehireOu: "OU=Prehire,DC=example,DC=com",
            GraveyardOu: "OU=Graveyard,DC=example,DC=com",
            Transport: new ActiveDirectoryTransportConfig("ldap", false, false, false, [], true),
            IdentityPolicy: new ActiveDirectoryIdentityPolicyConfig(false));

        var message = Assert.IsType<string>(method!.Invoke(null, [command, config, "ldap"]));

        Assert.Equal("Created and enabled AD user user.10001 without initial password provisioning.", message);
    }
    [Fact]
    public void GenerateRandomPassword_ReturnsComplexPassword()
    {
        var method = typeof(ActiveDirectoryCommandGateway).GetMethod("GenerateRandomPassword", BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);

        var password = Assert.IsType<string>(method!.Invoke(null, [20]));

        Assert.Equal(20, password.Length);
        Assert.Contains(password, char.IsUpper);
        Assert.Contains(password, char.IsLower);
        Assert.Contains(password, char.IsDigit);
        Assert.Contains(password, character => "!@#$%^&*-_=+?".Contains(character, StringComparison.Ordinal));
    }

    [Fact]
    public void GenerateRandomPassword_AllowsExplicitFallbackLength()
    {
        var method = typeof(ActiveDirectoryCommandGateway).GetMethod("GenerateRandomPassword", BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);

        var password = Assert.IsType<string>(method!.Invoke(null, [14]));

        Assert.Equal(14, password.Length);
        Assert.Contains(password, char.IsUpper);
        Assert.Contains(password, char.IsLower);
        Assert.Contains(password, char.IsDigit);
        Assert.Contains(password, character => "!@#$%^&*-_=+?".Contains(character, StringComparison.Ordinal));
    }

    [Fact]
    public void IsPasswordRestrictionFailure_ReturnsTrueForDirectoryOperationException()
    {
        var method = typeof(ActiveDirectoryCommandGateway).GetMethod("IsPasswordRestrictionFailure", BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);

        var exception = new DirectoryOperationException(
            "The server cannot handle directory requests. 0000052D: SvcErr: DSID-031A126C, problem 5003 (WILL_NOT_PERFORM), data 0");

        var result = Assert.IsType<bool>(method!.Invoke(null, [exception]));

        Assert.True(result);
    }

    [Fact]
    public void IsPasswordRestrictionFailure_ReturnsTrueForLdapServerErrorMessage()
    {
        var method = typeof(ActiveDirectoryCommandGateway).GetMethod("IsPasswordRestrictionFailure", BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);

        var exception = new LdapException(
            53,
            "The server cannot handle directory requests.",
            "0000052D: SvcErr: DSID-031A126C, problem 5003 (WILL_NOT_PERFORM), data 0");

        var result = Assert.IsType<bool>(method!.Invoke(null, [exception]));

        Assert.True(result);
    }

    [Fact]
    public void EncodeUnicodePassword_WrapsPasswordInQuotesAndUsesUtf16()
    {
        var method = typeof(ActiveDirectoryCommandGateway).GetMethod("EncodeUnicodePassword", BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);

        var bytes = Assert.IsType<byte[]>(method!.Invoke(null, ["Abc123!xyz"]));

        Assert.Equal("\"Abc123!xyz\"", System.Text.Encoding.Unicode.GetString(bytes));
    }

    [Fact]
    public void BuildUpdateModifications_PrefersMappedIdentityAttributeValue()
    {
        var method = typeof(ActiveDirectoryCommandGateway).GetMethod("BuildUpdateModifications", BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);

        var command = new DirectoryMutationCommand(
            Action: "UpdateUser",
            WorkerId: "10001",
            ManagerId: null,
            ManagerDistinguishedName: null,
            SamAccountName: "user.10001",
            CommonName: "user.10001",
            UserPrincipalName: "user.10001@example.com",
            Mail: "user.10001@example.com",
            TargetOu: "OU=Users,DC=example,DC=com",
            DisplayName: "Sample, User",
            CurrentDistinguishedName: "CN=user.10001,OU=Users,DC=example,DC=com",
            EnableAccount: true,
            Operations: [new SyncFactors.Contracts.DirectoryOperation("UpdateUser")],
            Attributes: new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
            {
                ["employeeID"] = "20002",
                ["department"] = "IT"
            });
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
            IdentityPolicy: new ActiveDirectoryIdentityPolicyConfig(false));

        var modifications = Assert.IsType<DirectoryAttributeModificationCollection>(method!.Invoke(null, [command, config, null]));

        var employeeIdModifications = modifications.Cast<DirectoryAttributeModification>()
            .Where(modification => string.Equals(modification.Name, "employeeID", StringComparison.OrdinalIgnoreCase))
            .ToArray();
        Assert.Single(employeeIdModifications);
        Assert.Equal("20002", employeeIdModifications[0].GetValues(typeof(string)).Cast<string>().Single());
    }

    [Fact]
    public void BuildUpdateModifications_DoesNotOverwriteIdentityAttributeWhenUnchanged()
    {
        var method = typeof(ActiveDirectoryCommandGateway).GetMethod("BuildUpdateModifications", BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);

        var command = new DirectoryMutationCommand(
            Action: "UpdateUser",
            WorkerId: "user.10001",
            ManagerId: null,
            ManagerDistinguishedName: null,
            SamAccountName: "user.10001",
            CommonName: "user.10001",
            UserPrincipalName: "user.10001@example.com",
            Mail: "user.10001@example.com",
            TargetOu: "OU=Users,DC=example,DC=com",
            DisplayName: "Sample, User",
            CurrentDistinguishedName: "CN=user.10001,OU=Users,DC=example,DC=com",
            EnableAccount: true,
            Operations: [new SyncFactors.Contracts.DirectoryOperation("UpdateUser")],
            Attributes: new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
            {
                ["department"] = "IT"
            });
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
            IdentityPolicy: new ActiveDirectoryIdentityPolicyConfig(false));

        var modifications = Assert.IsType<DirectoryAttributeModificationCollection>(method!.Invoke(null, [command, config, null]));

        Assert.DoesNotContain(
            modifications.Cast<DirectoryAttributeModification>(),
            modification => string.Equals(modification.Name, "employeeID", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void ResolveIdentityLookupValue_PrefersMappedIdentityAttributeValue()
    {
        var method = typeof(ActiveDirectoryCommandGateway).GetMethod("ResolveIdentityLookupValue", BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);

        var command = new DirectoryMutationCommand(
            Action: "UpdateUser",
            WorkerId: "user.10001",
            ManagerId: null,
            ManagerDistinguishedName: null,
            SamAccountName: "user.10001",
            CommonName: "user.10001",
            UserPrincipalName: "user.10001@example.com",
            Mail: "user.10001@example.com",
            TargetOu: "OU=Users,DC=example,DC=com",
            DisplayName: "Sample, User",
            CurrentDistinguishedName: "CN=user.10001,OU=Users,DC=example,DC=com",
            EnableAccount: true,
            Operations: [new SyncFactors.Contracts.DirectoryOperation("UpdateUser")],
            Attributes: new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
            {
                ["employeeID"] = "10001"
            });
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
            IdentityPolicy: new ActiveDirectoryIdentityPolicyConfig(false));

        var identityValue = Assert.IsType<string>(method!.Invoke(null, [command, config]));

        Assert.Equal("10001", identityValue);
    }

    [Fact]
    public void ResolveIdentityLookupValue_FallsBackToWorkerId()
    {
        var method = typeof(ActiveDirectoryCommandGateway).GetMethod("ResolveIdentityLookupValue", BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);

        var command = new DirectoryMutationCommand(
            Action: "UpdateUser",
            WorkerId: "user.10001",
            ManagerId: null,
            ManagerDistinguishedName: null,
            SamAccountName: "user.10001",
            CommonName: "user.10001",
            UserPrincipalName: "user.10001@example.com",
            Mail: "user.10001@example.com",
            TargetOu: "OU=Users,DC=example,DC=com",
            DisplayName: "Sample, User",
            CurrentDistinguishedName: "CN=user.10001,OU=Users,DC=example,DC=com",
            EnableAccount: true,
            Operations: [new SyncFactors.Contracts.DirectoryOperation("UpdateUser")],
            Attributes: new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
            {
                ["department"] = "IT"
            });
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
            IdentityPolicy: new ActiveDirectoryIdentityPolicyConfig(false));

        var identityValue = Assert.IsType<string>(method!.Invoke(null, [command, config]));

        Assert.Equal("user.10001", identityValue);
    }

    [Theory]
    [InlineData("sAMAccountName", "user.10001")]
    [InlineData("userPrincipalName", "user.10001@example.com")]
    [InlineData("mail", "user.10001@example.com")]
    public void ResolveIdentityLookupValue_FallsBackToCommandIdentityForStandardAttributes(string identityAttribute, string expectedValue)
    {
        var method = typeof(ActiveDirectoryCommandGateway).GetMethod("ResolveIdentityLookupValue", BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);

        var command = new DirectoryMutationCommand(
            Action: "UpdateUser",
            WorkerId: "10001",
            ManagerId: null,
            ManagerDistinguishedName: null,
            SamAccountName: "user.10001",
            CommonName: "user.10001",
            UserPrincipalName: "user.10001@example.com",
            Mail: "user.10001@example.com",
            TargetOu: "OU=Users,DC=example,DC=com",
            DisplayName: "Sample, User",
            CurrentDistinguishedName: "CN=user.10001,OU=Users,DC=example,DC=com",
            EnableAccount: true,
            Operations: [new SyncFactors.Contracts.DirectoryOperation("UpdateUser")],
            Attributes: new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
            {
                ["department"] = "IT"
            });
        var config = new ActiveDirectoryConfig(
            Server: "localhost",
            Port: 389,
            Username: "bind",
            BindPassword: "secret",
            IdentityAttribute: identityAttribute,
            DefaultActiveOu: "OU=Active,DC=example,DC=com",
            PrehireOu: "OU=Prehire,DC=example,DC=com",
            GraveyardOu: "OU=Graveyard,DC=example,DC=com",
            Transport: new ActiveDirectoryTransportConfig("ldap", false, false, false, []),
            IdentityPolicy: new ActiveDirectoryIdentityPolicyConfig(false));

        var identityValue = Assert.IsType<string>(method!.Invoke(null, [command, config]));

        Assert.Equal(expectedValue, identityValue);
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

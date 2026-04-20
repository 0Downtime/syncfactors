using System.DirectoryServices.Protocols;
using System.Reflection;
using SyncFactors.Infrastructure;

namespace SyncFactors.Infrastructure.Tests;

public sealed class ExternalSystemExceptionFactoryTests
{
    [Fact]
    public void CreateActiveDirectoryValidationException_FormatsStructuredMessage()
    {
        var method = typeof(SyncFactorsConfigurationLoader).Assembly
            .GetType("SyncFactors.Infrastructure.ExternalSystemExceptionFactory")
            ?.GetMethod(
                "CreateActiveDirectoryValidationException",
                BindingFlags.Public | BindingFlags.Static,
                [typeof(string), typeof(ActiveDirectoryConfig), typeof(string), typeof(string), typeof(string)]);
        Assert.NotNull(method);

        var config = new ActiveDirectoryConfig(
            Server: "192.0.2.10",
            Port: 389,
            Username: "svc_syncfactors@example.local",
            BindPassword: "secret",
            IdentityAttribute: "employeeID",
            DefaultActiveOu: "OU=Users,DC=example,DC=com",
            PrehireOu: "OU=Prehire,DC=example,DC=com",
            GraveyardOu: "OU=Graveyard,DC=example,DC=com",
            Transport: new ActiveDirectoryTransportConfig(
                Mode: "ldap",
                AllowLdapFallback: false,
                RequireCertificateValidation: false,
                RequireSigning: false,
                TrustedCertificateThumbprints: []),
            IdentityPolicy: new ActiveDirectoryIdentityPolicyConfig(false));

        var exception = Assert.IsType<InvalidOperationException>(method!.Invoke(null, ["command 'CreateUser'", config, "A different AD account already uses userPrincipalName 'brian.oliver@Exampleenergy.com' for create worker 45086.", "Step=PreflightIdentityConflict WorkerId=45086", "Resolve the existing AD account that already owns this UPN or mail value, or change the planned suffix/value before retrying."]));

        Assert.Contains("Active Directory command 'CreateUser' failed against LDAP server '192.0.2.10'.", exception.Message, StringComparison.Ordinal);
        Assert.Contains("A different AD account already uses userPrincipalName 'brian.oliver@Exampleenergy.com' for create worker 45086.", exception.Message, StringComparison.Ordinal);
        Assert.Contains("Details: Step=PreflightIdentityConflict WorkerId=45086", exception.Message, StringComparison.Ordinal);
        Assert.Contains("Next check: Resolve the existing AD account", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void CreateActiveDirectoryException_IncludesConnectionSettingsAndHostPortHint_ForUnavailableServer()
    {
        var method = typeof(SyncFactorsConfigurationLoader).Assembly
            .GetType("SyncFactors.Infrastructure.ExternalSystemExceptionFactory")
            ?.GetMethod(
                "CreateActiveDirectoryException",
                BindingFlags.Public | BindingFlags.Static,
                [typeof(string), typeof(ActiveDirectoryConfig), typeof(Exception)]);
        Assert.NotNull(method);

        var config = new ActiveDirectoryConfig(
            Server: "localhost:389",
            Port: null,
            Username: "svc_syncfactors@example.local",
            BindPassword: "secret",
            IdentityAttribute: "employeeID",
            DefaultActiveOu: "OU=Users,DC=example,DC=com",
            PrehireOu: "OU=Prehire,DC=example,DC=com",
            GraveyardOu: "OU=Graveyard,DC=example,DC=com",
            Transport: new ActiveDirectoryTransportConfig(
                Mode: "ldaps",
                AllowLdapFallback: false,
                RequireCertificateValidation: true,
                RequireSigning: true,
                TrustedCertificateThumbprints: []),
            IdentityPolicy: new ActiveDirectoryIdentityPolicyConfig(false));

        var exception = Assert.IsType<InvalidOperationException>(method!.Invoke(null, ["lookup", config, new LdapException("The LDAP server is unavailable.")]));

        Assert.Contains("Connection settings: host='localhost:389', port=636, transport=ldaps.", exception.Message, StringComparison.Ordinal);
        Assert.Contains("Current config: host='localhost:389', port=636, transport=ldaps.", exception.Message, StringComparison.Ordinal);
        Assert.Contains("include a port in the host field", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void CreateActiveDirectoryException_ReportsMissingOuGuidance_ForNoObjectDirectoryOperation()
    {
        var method = typeof(SyncFactorsConfigurationLoader).Assembly
            .GetType("SyncFactors.Infrastructure.ExternalSystemExceptionFactory")
            ?.GetMethod(
                "CreateActiveDirectoryException",
                BindingFlags.Public | BindingFlags.Static,
                [typeof(string), typeof(ActiveDirectoryConfig), typeof(Exception)]);
        Assert.NotNull(method);

        var config = new ActiveDirectoryConfig(
            Server: "localhost",
            Port: 389,
            Username: "svc_syncfactors@example.local",
            BindPassword: "secret",
            IdentityAttribute: "employeeID",
            DefaultActiveOu: "OU=POWERSHELL,OU=ExampleQA-Users,DC=ExampleQA,DC=biz",
            PrehireOu: "OU=Prehire,OU=ExampleQA-Users,DC=ExampleQA,DC=biz",
            GraveyardOu: "OU=GRAVEYARD,OU=ExampleQA-Users,DC=ExampleQA,DC=biz",
            Transport: new ActiveDirectoryTransportConfig(
                Mode: "ldap",
                AllowLdapFallback: false,
                RequireCertificateValidation: false,
                RequireSigning: false,
                TrustedCertificateThumbprints: []),
            IdentityPolicy: new ActiveDirectoryIdentityPolicyConfig(false),
            LeaveOu: "OU=LEAVE USERS,OU=ExampleQA-Users,DC=ExampleQA,DC=biz");

        var directoryException = new DirectoryOperationException(
            "The object does not exist. 0000208D: NameErr: DSID-0310028D, problem 2001 (NO_OBJECT), data 0, best match of:\n\t'OU=ExampleQA-Users,DC=ExampleQA,DC=biz'");

        var exception = Assert.IsType<InvalidOperationException>(method!.Invoke(null, ["lookup", config, directoryException]));

        Assert.Contains("The directory search base does not exist or is misconfigured.", exception.Message, StringComparison.Ordinal);
        Assert.Contains("Best match in AD was 'OU=ExampleQA-Users,DC=ExampleQA,DC=biz'.", exception.Message, StringComparison.Ordinal);
        Assert.Contains("Raw AD error: The object does not exist.", exception.Message, StringComparison.Ordinal);
        Assert.Contains("defaultActiveOu='OU=POWERSHELL,OU=ExampleQA-Users,DC=ExampleQA,DC=biz'", exception.Message, StringComparison.Ordinal);
        Assert.Contains("leaveOu='OU=LEAVE USERS,OU=ExampleQA-Users,DC=ExampleQA,DC=biz'", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void CreateActiveDirectoryException_ForCreateUserNoObject_IncludesTargetDnAndRawAdMessage()
    {
        var method = typeof(SyncFactorsConfigurationLoader).Assembly
            .GetType("SyncFactors.Infrastructure.ExternalSystemExceptionFactory")
            ?.GetMethod(
                "CreateActiveDirectoryException",
                BindingFlags.Public | BindingFlags.Static,
                [typeof(string), typeof(ActiveDirectoryConfig), typeof(Exception), typeof(string)]);
        Assert.NotNull(method);

        var config = new ActiveDirectoryConfig(
            Server: "localhost",
            Port: 636,
            Username: "svc_successfactors@Exampleqa.biz",
            BindPassword: "secret",
            IdentityAttribute: "employeeID",
            DefaultActiveOu: "OU=POWERSHELL,OU=ExampleQA-Users,DC=ExampleQA,DC=biz",
            PrehireOu: "OU=Prehire,OU=ExampleQA-Users,DC=ExampleQA,DC=biz",
            GraveyardOu: "OU=GRAVEYARD,OU=ExampleQA-Users,DC=ExampleQA,DC=biz",
            Transport: new ActiveDirectoryTransportConfig(
                Mode: "ldaps",
                AllowLdapFallback: false,
                RequireCertificateValidation: true,
                RequireSigning: true,
                TrustedCertificateThumbprints: []),
            IdentityPolicy: new ActiveDirectoryIdentityPolicyConfig(false));

        var ldapException = new LdapException(
            32,
            "The object does not exist. 0000208D: NameErr: DSID-0310028D, problem 2001 (NO_OBJECT), data 0, best match of:\n\t'OU=POWERSHELL,OU=ExampleQA-Users,DC=ExampleQA,DC=biz'");
        var details = "Step=CreateUser WorkerId=30008382 SamAccountName=30008382 DistinguishedName=CN=30008382,OU=POWERSHELL,OU=ExampleQA-Users,DC=ExampleQA,DC=biz TargetOu=OU=POWERSHELL,OU=ExampleQA-Users,DC=ExampleQA,DC=biz";

        var exception = Assert.IsType<InvalidOperationException>(method!.Invoke(null, ["command 'CreateUser'", config, ldapException, details]));

        Assert.Contains("Active Directory reported NO_OBJECT while creating 'CN=30008382,OU=POWERSHELL,OU=ExampleQA-Users,DC=ExampleQA,DC=biz' under target OU 'OU=POWERSHELL,OU=ExampleQA-Users,DC=ExampleQA,DC=biz'.", exception.Message, StringComparison.Ordinal);
        Assert.Contains("Best match in AD was 'OU=POWERSHELL,OU=ExampleQA-Users,DC=ExampleQA,DC=biz'.", exception.Message, StringComparison.Ordinal);
        Assert.Contains("Raw AD error: The object does not exist.", exception.Message, StringComparison.Ordinal);
        Assert.Contains("Current create target DN='CN=30008382,OU=POWERSHELL,OU=ExampleQA-Users,DC=ExampleQA,DC=biz', targetOu='OU=POWERSHELL,OU=ExampleQA-Users,DC=ExampleQA,DC=biz'.", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void CreateActiveDirectoryException_IncludesLdapServerDetailAndErrorCode()
    {
        var method = typeof(SyncFactorsConfigurationLoader).Assembly
            .GetType("SyncFactors.Infrastructure.ExternalSystemExceptionFactory")
            ?.GetMethod(
                "CreateActiveDirectoryException",
                BindingFlags.Public | BindingFlags.Static,
                [typeof(string), typeof(ActiveDirectoryConfig), typeof(Exception)]);
        Assert.NotNull(method);

        var config = new ActiveDirectoryConfig(
            Server: "192.0.2.10",
            Port: 389,
            Username: "svc_syncfactors@example.local",
            BindPassword: "secret",
            IdentityAttribute: "employeeID",
            DefaultActiveOu: "OU=Users,DC=example,DC=com",
            PrehireOu: "OU=Prehire,DC=example,DC=com",
            GraveyardOu: "OU=Graveyard,DC=example,DC=com",
            Transport: new ActiveDirectoryTransportConfig(
                Mode: "ldap",
                AllowLdapFallback: false,
                RequireCertificateValidation: false,
                RequireSigning: false,
                TrustedCertificateThumbprints: []),
            IdentityPolicy: new ActiveDirectoryIdentityPolicyConfig(false));
        var ldapException = new LdapException(
            19,
            "A value in the request is invalid.",
            "000021C8: AtrErr: DSID-03200E96, problem 1005 (CONSTRAINT_ATT_TYPE), Att 90290 (userPrincipalName)");

        var exception = Assert.IsType<InvalidOperationException>(method!.Invoke(null, ["command 'CreateUser'", config, ldapException]));

        Assert.Contains("A value in the request is invalid.", exception.Message, StringComparison.Ordinal);
        Assert.Contains("LDAP error code 19.", exception.Message, StringComparison.Ordinal);
        Assert.Contains("Server detail: 000021C8: AtrErr: DSID-03200E96", exception.Message, StringComparison.Ordinal);
    }
}

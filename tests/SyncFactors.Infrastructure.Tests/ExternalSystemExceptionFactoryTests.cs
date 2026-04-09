using System.DirectoryServices.Protocols;
using System.Reflection;
using SyncFactors.Infrastructure;

namespace SyncFactors.Infrastructure.Tests;

public sealed class ExternalSystemExceptionFactoryTests
{
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
        Assert.Contains("defaultActiveOu='OU=POWERSHELL,OU=ExampleQA-Users,DC=ExampleQA,DC=biz'", exception.Message, StringComparison.Ordinal);
        Assert.Contains("leaveOu='OU=LEAVE USERS,OU=ExampleQA-Users,DC=ExampleQA,DC=biz'", exception.Message, StringComparison.Ordinal);
    }
}

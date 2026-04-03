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
}

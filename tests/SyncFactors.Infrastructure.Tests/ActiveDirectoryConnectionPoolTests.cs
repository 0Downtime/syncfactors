using System.DirectoryServices.Protocols;
using Microsoft.Extensions.Logging.Abstractions;

namespace SyncFactors.Infrastructure.Tests;

public sealed class ActiveDirectoryConnectionPoolTests
{
    [Fact]
    public void Lease_ReusesReturnedConnection()
    {
        var factoryCalls = 0;
        using var pool = new ActiveDirectoryConnectionPool(
            connectionFactory: (_, _, _) =>
            {
                factoryCalls++;
                return new LdapConnection(new LdapDirectoryIdentifier("localhost"));
            });

        LdapConnection firstConnection;
        using (var lease = pool.Lease(CreateConfig(), NullLogger.Instance, TimeSpan.FromSeconds(1)))
        {
            firstConnection = lease.Connection;
        }

        using var secondLease = pool.Lease(CreateConfig(), NullLogger.Instance, TimeSpan.FromSeconds(1));

        Assert.Same(firstConnection, secondLease.Connection);
        Assert.Equal(1, factoryCalls);
    }

    [Fact]
    public void Lease_DoesNotReuseInvalidatedConnection()
    {
        var factoryCalls = 0;
        using var pool = new ActiveDirectoryConnectionPool(
            connectionFactory: (_, _, _) =>
            {
                factoryCalls++;
                return new LdapConnection(new LdapDirectoryIdentifier("localhost"));
            });

        LdapConnection firstConnection;
        using (var lease = pool.Lease(CreateConfig(), NullLogger.Instance, TimeSpan.FromSeconds(1)))
        {
            firstConnection = lease.Connection;
            lease.Invalidate();
        }

        using var secondLease = pool.Lease(CreateConfig(), NullLogger.Instance, TimeSpan.FromSeconds(1));

        Assert.NotSame(firstConnection, secondLease.Connection);
        Assert.Equal(2, factoryCalls);
    }

    private static ActiveDirectoryConfig CreateConfig()
    {
        return new ActiveDirectoryConfig(
            Server: "ldap.example.test",
            Port: 636,
            Username: "svc_successfactors@example.local",
            BindPassword: "secret",
            IdentityAttribute: "employeeID",
            DefaultActiveOu: "OU=Employees,DC=example,DC=com",
            PrehireOu: "OU=Prehire,DC=example,DC=com",
            GraveyardOu: "OU=Graveyard,DC=example,DC=com",
            Transport: new ActiveDirectoryTransportConfig(
                Mode: "ldaps",
                AllowLdapFallback: false,
                RequireCertificateValidation: true,
                RequireSigning: true,
                TrustedCertificateThumbprints: []),
            IdentityPolicy: new ActiveDirectoryIdentityPolicyConfig(
                ResolveCreateConflictingUpnAndMail: false));
    }
}

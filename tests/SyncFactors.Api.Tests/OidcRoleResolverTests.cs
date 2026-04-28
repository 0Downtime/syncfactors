using System.Security.Claims;
using SyncFactors.Infrastructure;

namespace SyncFactors.Api.Tests;

public sealed class OidcRoleResolverTests
{
    [Fact]
    public void ResolveRoles_ReturnsNoRoles_WhenNoConfiguredGroupMatches()
    {
        var options = new LocalAuthOptions
        {
            Oidc =
            {
                ViewerGroups = ["viewer-group"],
                OperatorGroups = ["operator-group"],
                AdminGroups = ["admin-group"]
            }
        };
        var identity = BuildIdentity("unmapped-group");

        var roles = OidcRoleResolver.ResolveRoles(identity, options);

        Assert.Empty(roles);
    }

    [Fact]
    public void ResolveRoles_ReturnsMatchingConfiguredRoles()
    {
        var options = new LocalAuthOptions
        {
            Oidc =
            {
                ViewerGroups = ["viewer-group"],
                OperatorGroups = ["operator-group"],
                AdminGroups = ["admin-group"]
            }
        };
        var identity = BuildIdentity("viewer-group", "operator-group", "admin-group");

        var roles = OidcRoleResolver.ResolveRoles(identity, options);

        Assert.Contains(SecurityRoles.Viewer, roles);
        Assert.Contains(SecurityRoles.Operator, roles);
        Assert.Contains(SecurityRoles.Admin, roles);
    }

    [Fact]
    public void ResolveRoles_UsesConfiguredClaimType()
    {
        var options = new LocalAuthOptions
        {
            Oidc =
            {
                RolesClaimType = "roles",
                OperatorGroups = ["sync-operator"]
            }
        };
        var identity = new ClaimsIdentity(
        [
            new Claim("groups", "sync-operator"),
            new Claim("roles", "sync-operator")
        ]);

        var roles = OidcRoleResolver.ResolveRoles(identity, options);

        Assert.Equal([SecurityRoles.Operator], roles);
    }

    [Fact]
    public void HasConfiguredRoleGroups_RequiresAtLeastOneNonEmptyMapping()
    {
        Assert.False(OidcRoleResolver.HasConfiguredRoleGroups(new LocalAuthOptions()));

        var options = new LocalAuthOptions
        {
            Oidc =
            {
                AdminGroups = ["admin-group"]
            }
        };

        Assert.True(OidcRoleResolver.HasConfiguredRoleGroups(options));
    }

    private static ClaimsIdentity BuildIdentity(params string[] groups) =>
        new(groups.Select(group => new Claim("groups", group)));
}

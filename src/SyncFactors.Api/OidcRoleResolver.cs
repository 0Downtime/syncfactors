using System.Security.Claims;
using SyncFactors.Infrastructure;

namespace SyncFactors.Api;

internal static class OidcRoleResolver
{
    public static IReadOnlyList<string> ResolveRoles(ClaimsIdentity identity, LocalAuthOptions authSettings)
    {
        var groups = identity.FindAll(authSettings.Oidc.RolesClaimType)
            .Select(claim => claim.Value)
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var roles = new List<string>();
        if (MatchesAny(groups, authSettings.Oidc.AdminGroups))
        {
            roles.Add(SecurityRoles.Admin);
        }

        if (MatchesAny(groups, authSettings.Oidc.OperatorGroups))
        {
            roles.Add(SecurityRoles.Operator);
        }

        if (MatchesAny(groups, authSettings.Oidc.ViewerGroups))
        {
            roles.Add(SecurityRoles.Viewer);
        }

        return roles.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    public static bool HasConfiguredRoleGroups(LocalAuthOptions authSettings) =>
        HasAny(authSettings.Oidc.ViewerGroups) ||
        HasAny(authSettings.Oidc.OperatorGroups) ||
        HasAny(authSettings.Oidc.AdminGroups);

    private static bool MatchesAny(HashSet<string> groups, IEnumerable<string> candidates) =>
        candidates.Any(candidate => !string.IsNullOrWhiteSpace(candidate) && groups.Contains(candidate));

    private static bool HasAny(IEnumerable<string> values) =>
        values.Any(value => !string.IsNullOrWhiteSpace(value));
}

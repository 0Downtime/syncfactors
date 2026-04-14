using System.Text.Json;
using System.Net;

namespace SyncFactors.Infrastructure;

public sealed class SyncFactorsConfigurationValidator(SyncFactorsConfigurationLoader loader)
{
    public void Validate()
    {
        var sync = loader.GetSyncConfig();
        var mapping = loader.GetMappingConfig();
        var environmentName = Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT")
            ?? Environment.GetEnvironmentVariable("DOTNET_ENVIRONMENT")
            ?? "Production";
        var isDevelopment = string.Equals(environmentName, "Development", StringComparison.OrdinalIgnoreCase);

        if (!Uri.TryCreate(sync.SuccessFactors.BaseUrl, UriKind.Absolute, out _))
        {
            throw new InvalidOperationException("SyncFactors successFactors.baseUrl must be an absolute URI.");
        }

        if (string.IsNullOrWhiteSpace(sync.Ad.Server))
        {
            throw new InvalidOperationException("SyncFactors AD server must be configured.");
        }

        if (!isDevelopment && IsLoopbackHost(sync.Ad.Server))
        {
            throw new InvalidOperationException("SyncFactors AD server must not resolve to localhost or a loopback address outside Development.");
        }

        if (string.IsNullOrWhiteSpace(sync.Ad.DefaultActiveOu) ||
            string.IsNullOrWhiteSpace(sync.Ad.PrehireOu) ||
            string.IsNullOrWhiteSpace(sync.Ad.GraveyardOu))
        {
            throw new InvalidOperationException("SyncFactors AD active, prehire, and graveyard OUs must be configured.");
        }

        ValidateManagedOuNamingContexts(sync.Ad);

        if ((sync.Sync.LeaveStatusValues?.Count ?? 0) > 0 && string.IsNullOrWhiteSpace(sync.Ad.LeaveOu))
        {
            throw new InvalidOperationException("SyncFactors AD leaveOu must be configured when sync.leaveStatusValues are set.");
        }

        if (!string.Equals(sync.Ad.Transport.Mode, "ldaps", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(sync.Ad.Transport.Mode, "starttls", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(sync.Ad.Transport.Mode, "ldap", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("SyncFactors AD transport.mode must be either 'ldaps', 'starttls', or 'ldap'.");
        }

        if (sync.Ad.Transport.AllowLdapFallback &&
            string.Equals(sync.Ad.Transport.Mode, "ldap", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("SyncFactors AD transport.allowLdapFallback cannot be enabled when transport.mode is already 'ldap'.");
        }

        if (!isDevelopment && !sync.Ad.Transport.RequireCertificateValidation)
        {
            throw new InvalidOperationException("SyncFactors AD transport.requireCertificateValidation must be enabled outside Development.");
        }

        if (!isDevelopment && !sync.Ad.Transport.RequireSigning)
        {
            throw new InvalidOperationException("SyncFactors AD transport.requireSigning must be enabled outside Development.");
        }

        if (sync.Sync.EnableBeforeStartDays < 0 || sync.Sync.DeletionRetentionDays < 0)
        {
            throw new InvalidOperationException("SyncFactors sync policy values must be non-negative.");
        }

        if (sync.Safety.MaxCreatesPerRun <= 0 || sync.Safety.MaxDisablesPerRun <= 0 || sync.Safety.MaxDeletionsPerRun <= 0)
        {
            throw new InvalidOperationException("SyncFactors safety thresholds must be positive.");
        }

        if (sync.Alerts.GraveyardRetentionReport.IntervalDays <= 0)
        {
            throw new InvalidOperationException("SyncFactors alerts.graveyardRetentionReport.intervalDays must be positive.");
        }

        if (sync.Alerts.Enabled && sync.Alerts.GraveyardRetentionReport.Enabled)
        {
            if (sync.Alerts.Smtp is null)
            {
                throw new InvalidOperationException("SyncFactors alerts.smtp must be configured when graveyard retention reports are enabled.");
            }

            if (sync.Alerts.Smtp.To.Count == 0)
            {
                throw new InvalidOperationException("SyncFactors alerts.smtp.to must contain at least one recipient when graveyard retention reports are enabled.");
            }
        }

        if (sync.SuccessFactors.Query.DeltaOverlapMinutes < 0)
        {
            throw new InvalidOperationException("SyncFactors successFactors.query.deltaOverlapMinutes must be non-negative.");
        }

        if (mapping.Mappings.Count == 0)
        {
            throw new InvalidOperationException("SyncFactors mapping config must contain at least one mapping.");
        }

        if (!mapping.Mappings.Any(mapping => mapping.Enabled))
        {
            throw new InvalidOperationException("SyncFactors mapping config must contain at least one enabled mapping.");
        }

        if (!isDevelopment)
        {
            ValidateNoProductionLiteralSecrets(loader.GetResolvedSyncConfigPath());
        }
    }

    private static void ValidateNoProductionLiteralSecrets(string configPath)
    {
        var document = JsonDocument.Parse(File.ReadAllText(configPath)).RootElement;
        var auth = document.GetRequiredObject("successFactors").GetRequiredObject("auth");
        var basic = auth.TryGetObject("basic", out var basicAuth) ? basicAuth : default;
        var oauth = auth.TryGetObject("oauth", out var oauthAuth) ? oauthAuth : default;
        var ad = document.GetRequiredObject("ad");

        var basicUsernameLiteral = basic.ValueKind == JsonValueKind.Object ? basic.TryGetString("username") : null;
        var basicPasswordLiteral = basic.ValueKind == JsonValueKind.Object ? basic.TryGetString("password") : null;
        var oauthClientIdLiteral = oauth.ValueKind == JsonValueKind.Object ? oauth.TryGetString("clientId") : null;
        var oauthClientSecretLiteral = oauth.ValueKind == JsonValueKind.Object ? oauth.TryGetString("clientSecret") : null;
        var adBindPasswordLiteral = ad.TryGetString("bindPassword");

        if (!string.IsNullOrWhiteSpace(basicUsernameLiteral))
        {
            throw new InvalidOperationException("Production config must not include a literal SuccessFactors username.");
        }

        if (!string.IsNullOrWhiteSpace(basicPasswordLiteral))
        {
            throw new InvalidOperationException("Production config must not include a literal SuccessFactors password.");
        }

        if (!string.IsNullOrWhiteSpace(oauthClientIdLiteral))
        {
            throw new InvalidOperationException("Production config must not include a literal SuccessFactors OAuth client ID.");
        }

        if (!string.IsNullOrWhiteSpace(oauthClientSecretLiteral))
        {
            throw new InvalidOperationException("Production config must not include a literal SuccessFactors OAuth client secret.");
        }

        if (!string.IsNullOrWhiteSpace(adBindPasswordLiteral))
        {
            throw new InvalidOperationException("Production config must not include a literal AD bind password.");
        }

        if (string.Equals(basicUsernameLiteral, "replace-me", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(basicPasswordLiteral, "replace-me", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(oauthClientIdLiteral, "replace-me", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(oauthClientSecretLiteral, "replace-me", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(adBindPasswordLiteral, "replace-me", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(adBindPasswordLiteral, "Replace-This-Password123!", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Production config contains placeholder credentials.");
        }
    }

    private static bool IsLoopbackHost(string server)
    {
        var trimmed = server.Trim();
        if (string.IsNullOrWhiteSpace(trimmed))
        {
            return false;
        }

        if (string.Equals(trimmed, "localhost", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (Uri.CheckHostName(trimmed) == UriHostNameType.IPv6 &&
            IPAddress.TryParse(trimmed.Trim('[', ']'), out var ipv6Address))
        {
            return IPAddress.IsLoopback(ipv6Address);
        }

        if (IPAddress.TryParse(trimmed, out var address))
        {
            return IPAddress.IsLoopback(address);
        }

        return false;
    }

    private static void ValidateManagedOuNamingContexts(ActiveDirectoryConfig config)
    {
        var managedOus = new[]
        {
            ("defaultActiveOu", config.DefaultActiveOu),
            ("prehireOu", config.PrehireOu),
            ("graveyardOu", config.GraveyardOu),
            ("leaveOu", config.LeaveOu)
        }
        .Where(item => !string.IsNullOrWhiteSpace(item.Item2))
        .Select(item => (Name: item.Item1, DistinguishedName: item.Item2!, NamingContext: ExtractNamingContext(item.Item2!)))
        .ToArray();

        if (managedOus.Length <= 1)
        {
            return;
        }

        var expectedNamingContext = managedOus[0].NamingContext;
        var mismatched = managedOus
            .Where(item => !string.Equals(item.NamingContext, expectedNamingContext, StringComparison.OrdinalIgnoreCase))
            .ToArray();
        if (mismatched.Length == 0)
        {
            return;
        }

        var details = string.Join(
            ", ",
            managedOus.Select(item => $"{item.Name}='{item.DistinguishedName}'"));
        throw new InvalidOperationException(
            $"SyncFactors managed AD OUs must remain within the same naming context because LDAP MoveUser cannot cross domains. Current values: {details}.");
    }

    private static string ExtractNamingContext(string distinguishedName)
    {
        var parts = distinguishedName
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(part => part.StartsWith("DC=", StringComparison.OrdinalIgnoreCase))
            .ToArray();
        return string.Join(",", parts);
    }

}

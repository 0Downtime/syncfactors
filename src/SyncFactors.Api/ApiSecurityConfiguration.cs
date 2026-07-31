using System.Net;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.Extensions.Configuration;
using SyncFactors.Infrastructure;

namespace SyncFactors.Api;

internal sealed class AntiforgeryEndpointFilter(IAntiforgery antiforgery) : IEndpointFilter
{
    public async ValueTask<object?> InvokeAsync(
        EndpointFilterInvocationContext context,
        EndpointFilterDelegate next)
    {
        var request = context.HttpContext.Request;
        if (!HttpMethods.IsGet(request.Method) &&
            !HttpMethods.IsHead(request.Method) &&
            !HttpMethods.IsOptions(request.Method) &&
            !HttpMethods.IsTrace(request.Method))
        {
            try
            {
                await antiforgery.ValidateRequestAsync(context.HttpContext);
            }
            catch (AntiforgeryValidationException)
            {
                return Results.BadRequest(new
                {
                    error = "A valid antiforgery token is required for this request. Refresh the page and retry."
                });
            }
        }

        return await next(context);
    }
}

internal static class ApiAuthConfiguration
{
    public const string OidcClientSecretEnvironmentVariable = "SYNCFACTORS__AUTH__OIDC__CLIENTSECRET";
    public const string BootstrapAdminPasswordEnvironmentVariable = "SYNCFACTORS__AUTH__BOOTSTRAPADMIN__PASSWORD";

    private const string ModeKey = "SyncFactors:Auth:Mode";
    private const string LocalBreakGlassEnabledKey = "SyncFactors:Auth:LocalBreakGlass:Enabled";
    private const string OidcClientSecretKey = "SyncFactors:Auth:Oidc:ClientSecret";
    private const string BootstrapAdminPasswordKey = "SyncFactors:Auth:BootstrapAdmin:Password";

    public static LocalAuthOptions Bind(
        ConfigurationManager configuration,
        bool isDevelopment,
        ISyncFactorsSecretResolver secretResolver)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(secretResolver);

        var overrides = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        AddSecretOverride(
            overrides,
            OidcClientSecretKey,
            OidcClientSecretEnvironmentVariable,
            secretResolver);
        AddSecretOverride(
            overrides,
            BootstrapAdminPasswordKey,
            BootstrapAdminPasswordEnvironmentVariable,
            secretResolver);

        if (isDevelopment && string.IsNullOrWhiteSpace(configuration[ModeKey]))
        {
            overrides[ModeKey] = "local-break-glass";
            overrides[LocalBreakGlassEnabledKey] = "true";
        }

        if (overrides.Count > 0)
        {
            configuration.AddInMemoryCollection(overrides);
        }

        return configuration.GetSection("SyncFactors:Auth").Get<LocalAuthOptions>() ?? new LocalAuthOptions();
    }

    public static void Validate(LocalAuthOptions options, bool isDevelopment)
    {
        ArgumentNullException.ThrowIfNull(options);

        var mode = options.Mode?.Trim().ToLowerInvariant();
        if (mode is not ("local-break-glass" or "oidc" or "hybrid"))
        {
            throw new InvalidOperationException(
                "SyncFactors:Auth:Mode must be explicitly set to 'local-break-glass', 'oidc', or 'hybrid'.");
        }

        var localExpected = mode is "local-break-glass" or "hybrid";
        if (options.LocalBreakGlass.Enabled != localExpected)
        {
            throw new InvalidOperationException(
                localExpected
                    ? $"SyncFactors:Auth:LocalBreakGlass:Enabled must be true when auth mode is '{mode}'."
                    : "SyncFactors:Auth:LocalBreakGlass:Enabled must be false when auth mode is 'oidc'.");
        }

        var oidcExpected = mode is "oidc" or "hybrid";
        if (oidcExpected)
        {
            if (string.IsNullOrWhiteSpace(options.Oidc.Authority) ||
                string.IsNullOrWhiteSpace(options.Oidc.ClientId))
            {
                throw new InvalidOperationException(
                    $"SyncFactors:Auth mode '{mode}' requires OIDC authority and client ID.");
            }

            if (string.IsNullOrWhiteSpace(options.Oidc.ClientSecret))
            {
                throw new InvalidOperationException(
                    $"SyncFactors:Auth mode '{mode}' requires an OIDC client secret from environment or the service account's Windows Credential Manager.");
            }

            if (!OidcRoleResolver.HasConfiguredRoleGroups(options))
            {
                throw new InvalidOperationException(
                    "SyncFactors:Auth:Oidc must configure at least one ViewerGroups, OperatorGroups, or AdminGroups value when OIDC is enabled.");
            }
        }

        OidcConfigurationValidator.ValidateAuthority(options.Oidc, oidcExpected, isDevelopment);

        if (options.Oidc.AuthorizationRevalidationMinutes is
            < OidcOptions.MinAuthorizationRevalidationMinutes or
            > OidcOptions.MaxAuthorizationRevalidationMinutes)
        {
            throw new InvalidOperationException(
                $"SyncFactors:Auth:Oidc:AuthorizationRevalidationMinutes must be between {OidcOptions.MinAuthorizationRevalidationMinutes} and {OidcOptions.MaxAuthorizationRevalidationMinutes}.");
        }

        if (options.IdleTimeoutMinutes is < LocalAuthOptions.MinIdleTimeoutMinutes or > LocalAuthOptions.MaxIdleTimeoutMinutes)
        {
            throw new InvalidOperationException(
                $"SyncFactors:Auth:IdleTimeoutMinutes must be between {LocalAuthOptions.MinIdleTimeoutMinutes} and {LocalAuthOptions.MaxIdleTimeoutMinutes}.");
        }

        if (options.AbsoluteSessionHours is < LocalAuthOptions.MinAbsoluteSessionHours or > LocalAuthOptions.MaxAbsoluteSessionHours)
        {
            throw new InvalidOperationException(
                $"SyncFactors:Auth:AbsoluteSessionHours must be between {LocalAuthOptions.MinAbsoluteSessionHours} and {LocalAuthOptions.MaxAbsoluteSessionHours}.");
        }

        if (options.RememberMeSessionHours is < LocalAuthOptions.MinRememberMeSessionHours or > LocalAuthOptions.MaxRememberMeSessionHours)
        {
            throw new InvalidOperationException(
                $"SyncFactors:Auth:RememberMeSessionHours must be between {LocalAuthOptions.MinRememberMeSessionHours} and {LocalAuthOptions.MaxRememberMeSessionHours}.");
        }
    }

    private static void AddSecretOverride(
        IDictionary<string, string?> overrides,
        string configurationKey,
        string environmentVariableName,
        ISyncFactorsSecretResolver secretResolver)
    {
        var secret = secretResolver.GetSecretValue(environmentVariableName);
        if (!string.IsNullOrWhiteSpace(secret))
        {
            overrides[configurationKey] = secret;
        }
    }
}

internal sealed class ForwardedHeadersSettings
{
    public bool Enabled { get; set; }

    public string[] KnownProxies { get; set; } = [];

    public string[] KnownNetworks { get; set; } = [];
}

internal static class ForwardedHeadersConfiguration
{
    public static void Validate(ForwardedHeadersSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        if (!settings.Enabled)
        {
            return;
        }

        var knownProxies = settings.KnownProxies ?? [];
        var knownNetworks = settings.KnownNetworks ?? [];

        if (knownProxies.Length == 0 && knownNetworks.Length == 0)
        {
            throw new InvalidOperationException(
                "SyncFactors:ForwardedHeaders requires at least one explicitly configured KnownProxies or KnownNetworks value when enabled.");
        }

        foreach (var proxy in knownProxies)
        {
            if (!IPAddress.TryParse(proxy, out _))
            {
                throw new InvalidOperationException(
                    $"SyncFactors:ForwardedHeaders:KnownProxies contains invalid IP address '{proxy}'.");
            }
        }

        foreach (var network in knownNetworks)
        {
            if (!System.Net.IPNetwork.TryParse(network, out _))
            {
                throw new InvalidOperationException(
                    $"SyncFactors:ForwardedHeaders:KnownNetworks contains invalid CIDR network '{network}'.");
            }
        }
    }

    public static void Configure(ForwardedHeadersOptions options, ForwardedHeadersSettings settings)
    {
        ArgumentNullException.ThrowIfNull(options);
        Validate(settings);

        options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
        options.ForwardLimit = 1;
        options.RequireHeaderSymmetry = true;
        options.KnownProxies.Clear();
        options.KnownIPNetworks.Clear();

        foreach (var proxy in settings.KnownProxies ?? [])
        {
            options.KnownProxies.Add(IPAddress.Parse(proxy));
        }

        foreach (var network in settings.KnownNetworks ?? [])
        {
            options.KnownIPNetworks.Add(System.Net.IPNetwork.Parse(network));
        }
    }
}

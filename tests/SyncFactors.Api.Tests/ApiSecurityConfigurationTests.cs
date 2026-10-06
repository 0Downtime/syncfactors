using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
using SyncFactors.Infrastructure;

namespace SyncFactors.Api.Tests;

public sealed class ApiSecurityConfigurationTests
{
    [Fact]
    public void Bind_ResolvesServiceAuthSecretsBeforeOptionsAreBound()
    {
        var configuration = CreateConfiguration(
            ("SyncFactors:Auth:Mode", "hybrid"),
            ("SyncFactors:Auth:LocalBreakGlass:Enabled", "true"),
            ("SyncFactors:Auth:Oidc:Authority", "https://login.example.test/tenant/v2.0"),
            ("SyncFactors:Auth:Oidc:ClientId", "client-id"),
            ("SyncFactors:Auth:Oidc:ViewerGroups:0", "viewer-group"));
        var resolver = new DictionarySecretResolver(new Dictionary<string, string>
        {
            [ApiAuthConfiguration.OidcClientSecretEnvironmentVariable] = "credential-manager-client-secret",
            [ApiAuthConfiguration.BootstrapAdminPasswordEnvironmentVariable] = "credential-manager-bootstrap-password"
        });

        var options = ApiAuthConfiguration.Bind(configuration, isDevelopment: false, resolver);

        Assert.Equal("credential-manager-client-secret", options.Oidc.ClientSecret);
        Assert.Equal("credential-manager-bootstrap-password", options.BootstrapAdmin.Password);
        Assert.Equal(
            [
                ApiAuthConfiguration.OidcClientSecretEnvironmentVariable,
                ApiAuthConfiguration.BootstrapAdminPasswordEnvironmentVariable
            ],
            resolver.RequestedNames);
    }

    [Fact]
    public void Bind_DefaultsBlankAuthModeOnlyInDevelopment()
    {
        var development = ApiAuthConfiguration.Bind(
            CreateConfiguration(),
            isDevelopment: true,
            new DictionarySecretResolver(new Dictionary<string, string>()));
        var production = ApiAuthConfiguration.Bind(
            CreateConfiguration(),
            isDevelopment: false,
            new DictionarySecretResolver(new Dictionary<string, string>()));

        Assert.Equal("local-break-glass", development.Mode);
        Assert.True(development.LocalBreakGlass.Enabled);
        Assert.True(string.IsNullOrWhiteSpace(production.Mode));
        Assert.False(production.LocalBreakGlass.Enabled);
    }

    [Fact]
    public void Validate_RejectsFreshProductionConfigurationWithoutExplicitAuthMode()
    {
        var options = new LocalAuthOptions();

        var exception = Assert.Throws<InvalidOperationException>(() =>
            ApiAuthConfiguration.Validate(options, isDevelopment: false));

        Assert.Contains("must be explicitly set", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Validate_DoesNotFallBackToLocal_WhenOidcConfigurationIsIncomplete()
    {
        var options = new LocalAuthOptions
        {
            Mode = "oidc",
            LocalBreakGlass = new LocalBreakGlassOptions { Enabled = false },
            Oidc = new OidcOptions()
        };

        var exception = Assert.Throws<InvalidOperationException>(() =>
            ApiAuthConfiguration.Validate(options, isDevelopment: false));

        Assert.Contains("requires OIDC authority and client ID", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Validate_AcceptsCompleteProductionOidcConfiguration()
    {
        var options = new LocalAuthOptions
        {
            Mode = "oidc",
            LocalBreakGlass = new LocalBreakGlassOptions { Enabled = false },
            Oidc = new OidcOptions
            {
                Authority = "https://login.example.test/tenant/v2.0",
                ClientId = "client-id",
                ClientSecret = "client-secret",
                ViewerGroups = ["viewer-group"]
            }
        };

        ApiAuthConfiguration.Validate(options, isDevelopment: false);
    }

    [Fact]
    public void ForwardedHeaders_DefaultDisabledConfigurationIsAccepted()
    {
        ForwardedHeadersConfiguration.Validate(new ForwardedHeadersSettings());
    }

    [Fact]
    public void ForwardedHeaders_EnabledConfigurationRequiresExplicitTrustBoundary()
    {
        var settings = new ForwardedHeadersSettings
        {
            Enabled = true,
            KnownProxies = null!,
            KnownNetworks = null!
        };

        var exception = Assert.Throws<InvalidOperationException>(() =>
            ForwardedHeadersConfiguration.Validate(settings));

        Assert.Contains("KnownProxies or KnownNetworks", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ForwardedHeaders_ConfiguresOnlyExplicitTrustedProxyAndNetwork()
    {
        var settings = new ForwardedHeadersSettings
        {
            Enabled = true,
            KnownProxies = ["192.0.2.10"],
            KnownNetworks = ["198.51.100.0/24"]
        };
        var options = new ForwardedHeadersOptions();

        ForwardedHeadersConfiguration.Configure(options, settings);

        Assert.Equal([IPAddress.Parse("192.0.2.10")], options.KnownProxies);
        Assert.Equal([IPNetwork.Parse("198.51.100.0/24")], options.KnownIPNetworks);
        Assert.True(options.RequireHeaderSymmetry);
        Assert.Equal(1, options.ForwardLimit);
    }

    private static ConfigurationManager CreateConfiguration(params (string Key, string? Value)[] values)
    {
        var configuration = new ConfigurationManager();
        configuration.AddInMemoryCollection(values.ToDictionary(pair => pair.Key, pair => pair.Value));
        return configuration;
    }

    private sealed class DictionarySecretResolver(IReadOnlyDictionary<string, string> secrets) : ISyncFactorsSecretResolver
    {
        public List<string> RequestedNames { get; } = [];

        public string? GetSecretValue(string? variableName)
        {
            if (variableName is null)
            {
                return null;
            }

            RequestedNames.Add(variableName);
            return secrets.GetValueOrDefault(variableName);
        }

        public string ResolveSourceLabel(string? variableName, string fallbackSource) => fallbackSource;
    }
}

using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Microsoft.Extensions.Configuration;
using SyncFactors.Api;

namespace SyncFactors.Api.Tests;

public sealed class TlsCertificateLoaderTests
{
    [Theory]
    [InlineData("portal.example.com", "portal.example.com", true)]
    [InlineData("portal.example.com", "*.example.com", true)]
    [InlineData("nested.portal.example.com", "*.example.com", false)]
    [InlineData("portal.example.com", "api.example.com", false)]
    public void HostMatchesCertificateName_HandlesExactAndWildcardNames(
        string host,
        string certificateName,
        bool expected)
    {
        Assert.Equal(expected, TlsCertificateLoader.HostMatchesCertificateName(host, certificateName));
    }

    [Fact]
    public void NormalizeThumbprint_RemovesSeparators()
    {
        Assert.Equal("ABCDEF1234", TlsCertificateLoader.NormalizeThumbprint("ab cd:ef 12 34"));
    }

    [Fact]
    public void NormalizeThumbprint_ReturnsEmptyForBlankInput()
    {
        Assert.Equal(string.Empty, TlsCertificateLoader.NormalizeThumbprint(" "));
    }

    [Fact]
    public void HasExplicitKestrelCertificate_DetectsConfiguration()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Kestrel:Certificates:Default:Path"] = "/tmp/cert.pfx"
            })
            .Build();

        Assert.True(TlsCertificateLoader.HasExplicitKestrelCertificate(configuration));
    }

    [Fact]
    public void HasExplicitKestrelCertificate_DetectsEnvironment()
    {
        const string variableName = "ASPNETCORE_Kestrel__Certificates__Default__Thumbprint";
        var originalValue = Environment.GetEnvironmentVariable(variableName);
        Environment.SetEnvironmentVariable(variableName, "ABCDEF");

        try
        {
            var configuration = new ConfigurationBuilder().Build();

            Assert.True(TlsCertificateLoader.HasExplicitKestrelCertificate(configuration));
        }
        finally
        {
            Environment.SetEnvironmentVariable(variableName, originalValue);
        }
    }

    [Fact]
    public void GetHostCandidates_UsesPublicHostAndHttpsUrls()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["SyncFactors:ApiPublicHost"] = "sync.example.com",
                ["urls"] = "https://0.0.0.0:5087;https://api.example.com:5087"
            })
            .Build();

        var candidates = TlsCertificateLoader.GetHostCandidates(configuration);

        Assert.Contains("sync.example.com", candidates);
        Assert.Contains("api.example.com", candidates);
        Assert.DoesNotContain("0.0.0.0", candidates);
    }

    [Fact]
    public void GetHostCandidates_UsesEnvironmentUrlsWhenConfigIsEmpty()
    {
        var originalUrls = Environment.GetEnvironmentVariable("ASPNETCORE_URLS");
        var originalPublicHost = Environment.GetEnvironmentVariable("SYNCFACTORS_API_PUBLIC_HOST");
        Environment.SetEnvironmentVariable("ASPNETCORE_URLS", "https://env.example.com:5087");
        Environment.SetEnvironmentVariable("SYNCFACTORS_API_PUBLIC_HOST", null);

        try
        {
            var configuration = new ConfigurationBuilder().Build();

            var candidates = TlsCertificateLoader.GetHostCandidates(configuration);

            Assert.Contains("env.example.com", candidates);
        }
        finally
        {
            Environment.SetEnvironmentVariable("ASPNETCORE_URLS", originalUrls);
            Environment.SetEnvironmentVariable("SYNCFACTORS_API_PUBLIC_HOST", originalPublicHost);
        }
    }

    [Fact]
    public void GetHostCandidates_DoesNotAddMachineNameForLoopbackBindingOnly()
    {
        var originalPublicHost = Environment.GetEnvironmentVariable("SYNCFACTORS_API_PUBLIC_HOST");
        Environment.SetEnvironmentVariable("SYNCFACTORS_API_PUBLIC_HOST", null);
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["urls"] = "https://127.0.0.1:5087"
            })
            .Build();

        try
        {
            var candidates = TlsCertificateLoader.GetHostCandidates(configuration);

            Assert.Contains("127.0.0.1", candidates);
            Assert.DoesNotContain(Environment.MachineName, candidates);
        }
        finally
        {
            Environment.SetEnvironmentVariable("SYNCFACTORS_API_PUBLIC_HOST", originalPublicHost);
        }
    }

    [Fact]
    public void AddHostCandidate_SkipsBlankWildcardAndDuplicateHosts()
    {
        var candidates = new List<string>();

        Assert.False(TlsCertificateLoader.AddHostCandidate(candidates, " "));
        Assert.False(TlsCertificateLoader.AddHostCandidate(candidates, "0.0.0.0"));
        Assert.True(TlsCertificateLoader.AddHostCandidate(candidates, "[api.example.com]."));
        Assert.True(TlsCertificateLoader.AddHostCandidate(candidates, "api.example.com"));

        Assert.Single(candidates);
        Assert.Equal("api.example.com", candidates[0]);
    }

    [Fact]
    public void CertificateMatchesAnyHost_UsesCommonName()
    {
        using var certificate = CreateCertificate("api.example.com");

        Assert.True(TlsCertificateLoader.CertificateMatchesAnyHost(certificate, ["api.example.com"]));
        Assert.False(TlsCertificateLoader.CertificateMatchesAnyHost(certificate, ["other.example.com"]));
    }

    [Fact]
    public void GetCertificateDnsNames_ReturnsCommonName()
    {
        using var certificate = CreateCertificate("api.example.com");

        Assert.Contains("api.example.com", TlsCertificateLoader.GetCertificateDnsNames(certificate));
    }

    [Fact]
    public void IsUsableServerCertificate_AcceptsServerAuthCertificate()
    {
        using var certificate = CreateCertificate("api.example.com", ["1.3.6.1.5.5.7.3.1"]);

        Assert.True(TlsCertificateLoader.IsUsableServerCertificate(certificate));
    }

    [Fact]
    public void IsUsableServerCertificate_AcceptsCertificateWithoutEnhancedKeyUsage()
    {
        using var certificate = CreateCertificate("api.example.com");

        Assert.True(TlsCertificateLoader.IsUsableServerCertificate(certificate));
    }

    [Fact]
    public void IsUsableServerCertificate_RejectsClientOnlyCertificate()
    {
        using var certificate = CreateCertificate("api.example.com", ["1.3.6.1.5.5.7.3.2"]);

        Assert.False(TlsCertificateLoader.IsUsableServerCertificate(certificate));
    }

    [Fact]
    public void IsUsableServerCertificate_RejectsExpiredCertificate()
    {
        using var certificate = CreateCertificate(
            "api.example.com",
            enhancedKeyUsageOids: ["1.3.6.1.5.5.7.3.1"],
            notBefore: DateTimeOffset.UtcNow.AddDays(-10),
            notAfter: DateTimeOffset.UtcNow.AddDays(-1));

        Assert.False(TlsCertificateLoader.IsUsableServerCertificate(certificate));
    }

    private static X509Certificate2 CreateCertificate(
        string commonName,
        string[]? enhancedKeyUsageOids = null,
        DateTimeOffset? notBefore = null,
        DateTimeOffset? notAfter = null)
    {
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest(
            $"CN={commonName}",
            rsa,
            HashAlgorithmName.SHA256,
            RSASignaturePadding.Pkcs1);

        if (enhancedKeyUsageOids is not null)
        {
            var usages = new OidCollection();
            foreach (var oid in enhancedKeyUsageOids)
            {
                usages.Add(new Oid(oid));
            }

            request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(usages, critical: false));
        }

        return request.CreateSelfSigned(
            notBefore ?? DateTimeOffset.UtcNow.AddDays(-1),
            notAfter ?? DateTimeOffset.UtcNow.AddDays(30));
    }
}

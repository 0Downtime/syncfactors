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
}

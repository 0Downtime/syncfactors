using System.Text.Json;

namespace SyncFactors.Infrastructure.Tests;

public sealed class SampleConfigSecretsTests
{
    [Fact]
    public void SampleRealConfig_DoesNotEmbedEnvironmentBackedCredentials()
    {
        var document = LoadConfig("sample.real-successfactors.real-ad.sync-config.json");

        Assert.Equal(string.Empty, document.RootElement.GetProperty("successFactors").GetProperty("auth").GetProperty("basic").GetProperty("username").GetString());
        Assert.Equal(string.Empty, document.RootElement.GetProperty("successFactors").GetProperty("auth").GetProperty("basic").GetProperty("password").GetString());
        Assert.Equal(string.Empty, document.RootElement.GetProperty("ad").GetProperty("server").GetString());
        Assert.Equal(string.Empty, document.RootElement.GetProperty("ad").GetProperty("username").GetString());
        Assert.Equal(string.Empty, document.RootElement.GetProperty("ad").GetProperty("bindPassword").GetString());
        Assert.Equal(string.Empty, document.RootElement.GetProperty("ad").GetProperty("defaultPassword").GetString());
    }

    [Fact]
    public void SampleMockConfig_DoesNotEmbedEnvironmentBackedCredentials()
    {
        var document = LoadConfig("sample.mock-successfactors.real-ad.sync-config.json");

        Assert.Equal(string.Empty, document.RootElement.GetProperty("successFactors").GetProperty("auth").GetProperty("oauth").GetProperty("clientId").GetString());
        Assert.Equal(string.Empty, document.RootElement.GetProperty("successFactors").GetProperty("auth").GetProperty("oauth").GetProperty("clientSecret").GetString());
        Assert.Equal(string.Empty, document.RootElement.GetProperty("ad").GetProperty("server").GetString());
        Assert.Equal(string.Empty, document.RootElement.GetProperty("ad").GetProperty("username").GetString());
        Assert.Equal(string.Empty, document.RootElement.GetProperty("ad").GetProperty("bindPassword").GetString());
        Assert.Equal(string.Empty, document.RootElement.GetProperty("ad").GetProperty("defaultPassword").GetString());
    }

    private static JsonDocument LoadConfig(string fileName)
    {
        var path = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "config", fileName));
        return JsonDocument.Parse(File.ReadAllText(path));
    }
}

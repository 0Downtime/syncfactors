using System.Security.Cryptography;

namespace SyncFactors.Infrastructure.Tests;

public sealed class RunCaptureMetadataProviderTests
{
    [Fact]
    public async Task Create_FingerprintsResolvedConfigFiles()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "syncfactors-run-capture", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);
        var syncConfigPath = Path.Combine(tempRoot, "sync.json");
        var mappingConfigPath = Path.Combine(tempRoot, "mapping.json");
        await File.WriteAllTextAsync(syncConfigPath, """{"sync":true}""");
        await File.WriteAllTextAsync(mappingConfigPath, """{"mappings":[]}""");
        var provider = new RunCaptureMetadataProvider(new SyncFactorsConfigPathResolver(syncConfigPath, mappingConfigPath));

        var metadata = provider.Create("run-1", dryRun: true, syncScope: "Full sync");

        Assert.Equal(1, metadata.SchemaVersion);
        Assert.Equal("run-1", metadata.RunId);
        Assert.True(metadata.DryRun);
        Assert.Equal("Full sync", metadata.SyncScope);
        Assert.Equal(Path.GetFullPath(syncConfigPath), metadata.SyncConfig?.Path);
        Assert.Equal(await ComputeSha256Async(syncConfigPath), metadata.SyncConfig?.Sha256);
        Assert.Equal(Path.GetFullPath(mappingConfigPath), metadata.MappingConfig?.Path);
        Assert.Equal(await ComputeSha256Async(mappingConfigPath), metadata.MappingConfig?.Sha256);
    }

    [Fact]
    public void Create_LeavesFingerprintNullWhenResolverFindsNoFile()
    {
        var missingConfigPath = Path.Combine(Path.GetTempPath(), "syncfactors-run-capture", Guid.NewGuid().ToString("N"), "missing.json");
        var provider = new RunCaptureMetadataProvider(new SyncFactorsConfigPathResolver(missingConfigPath, null));

        var metadata = provider.Create("run-2", dryRun: false, syncScope: "Bulk full scan");

        Assert.Null(metadata.SyncConfig);
    }

    private static async Task<string> ComputeSha256Async(string path)
    {
        await using var stream = File.OpenRead(path);
        return Convert.ToHexString(await SHA256.HashDataAsync(stream)).ToLowerInvariant();
    }
}

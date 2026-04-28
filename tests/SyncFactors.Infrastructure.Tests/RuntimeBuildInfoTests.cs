using System.Reflection;
using System.Reflection.Emit;

namespace SyncFactors.Infrastructure.Tests;

public sealed class RuntimeBuildInfoTests
{
    [Fact]
    public void FromAssembly_ReturnsFullCommitShaAndDirtyFlag_FromAssemblyMetadata()
    {
        var assembly = CreateAssembly(
            informationalVersion: "0.1.770+sha.77184d6.dirty",
            metadata:
            [
                new("SourceRevisionId", "77184d6e8ebb3c9712b2a7917568e6d8c11b892c"),
                new("SyncFactorsBuildDirty", "true")
            ]);

        var info = RuntimeBuildInfo.FromAssembly(assembly);

        Assert.Equal("0.1.770+sha.77184d6.dirty", info.Version);
        Assert.Equal("77184d6e8ebb3c9712b2a7917568e6d8c11b892c", info.CommitSha);
        Assert.Equal("77184d6", info.ShortCommitSha);
        Assert.True(info.Dirty);
        Assert.Equal(assembly.GetName().Name, info.AssemblyName);
    }

    [Fact]
    public void FromAssembly_ParsesShortSha_FromInformationalVersion_WhenMetadataIsUnavailable()
    {
        var assembly = CreateAssembly(
            informationalVersion: "0.1.770-dev.123+sha.77184d6",
            metadata: []);

        var info = RuntimeBuildInfo.FromAssembly(assembly);

        Assert.Equal("77184d6", info.CommitSha);
        Assert.Equal("77184d6", info.ShortCommitSha);
        Assert.False(info.Dirty);
    }

    private static Assembly CreateAssembly(string informationalVersion, IReadOnlyList<KeyValuePair<string, string>> metadata)
    {
        var assemblyName = new AssemblyName($"RuntimeBuildInfoTests.{Guid.NewGuid():N}");
        var assemblyBuilder = AssemblyBuilder.DefineDynamicAssembly(assemblyName, AssemblyBuilderAccess.Run);
        var informationalVersionConstructor = typeof(AssemblyInformationalVersionAttribute).GetConstructor([typeof(string)])
            ?? throw new InvalidOperationException("AssemblyInformationalVersionAttribute constructor was not found.");
        assemblyBuilder.SetCustomAttribute(new CustomAttributeBuilder(informationalVersionConstructor, [informationalVersion]));

        var metadataConstructor = typeof(AssemblyMetadataAttribute).GetConstructor([typeof(string), typeof(string)])
            ?? throw new InvalidOperationException("AssemblyMetadataAttribute constructor was not found.");
        foreach (var item in metadata)
        {
            assemblyBuilder.SetCustomAttribute(new CustomAttributeBuilder(metadataConstructor, [item.Key, item.Value]));
        }

        return assemblyBuilder;
    }
}

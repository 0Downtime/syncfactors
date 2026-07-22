using SyncFactors.Api;
using SyncFactors.Infrastructure;

namespace SyncFactors.Api.Tests;

public sealed class DirectoryServiceRuntimeSelectorTests
{
    [Fact]
    public void UseScaffoldDirectoryServices_ReturnsTrue_ForMockProfile()
    {
        Assert.True(DirectoryServiceRuntimeSelector.UseScaffoldDirectoryServices("mock"));
    }

    [Fact]
    public void UseScaffoldDirectoryServices_ReturnsFalse_ForRealProfile()
    {
        Assert.False(DirectoryServiceRuntimeSelector.UseScaffoldDirectoryServices("real"));
    }

    [Fact]
    public void UseScaffoldDirectoryServices_RejectsMissingRunProfile()
    {
        Assert.Throws<InvalidOperationException>(() => DirectoryServiceRuntimeSelector.UseScaffoldDirectoryServices(null));
    }
}

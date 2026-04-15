using SyncFactors.Domain;

namespace SyncFactors.Domain.Tests;

public sealed class DirectoryIdentityFormatterTests
{
    [Fact]
    public void BuildPreferredEmailLocalPart_UsesNameBasedValueWhenAvailable()
    {
        var localPart = DirectoryIdentityFormatter.BuildPreferredEmailLocalPart("Winnie", "Sample101", "45086");

        Assert.Equal("winnie.sample101", localPart);
    }

    [Fact]
    public void BuildPreferredEmailLocalPart_FallsBackToWorkerIdWhenNamesCollapse()
    {
        var localPart = DirectoryIdentityFormatter.BuildPreferredEmailLocalPart("!!!", "???", "45086");

        Assert.Equal("45086", localPart);
    }
}

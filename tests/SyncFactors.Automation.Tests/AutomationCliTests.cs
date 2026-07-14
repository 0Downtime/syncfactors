using SyncFactors.Automation;

namespace SyncFactors.Automation.Tests;

public sealed class AutomationCliTests
{
    [Fact]
    public async Task RunAsync_ReturnsFailureAndWritesUserFacingError_ForUnsupportedArgument()
    {
        using var output = new StringWriter();

        var exitCode = await AutomationCli.RunAsync(
            ["unexpected-argument"],
            output,
            CancellationToken.None);

        Assert.Equal(1, exitCode);
        Assert.Contains("Automation failed: Unsupported argument 'unexpected-argument'.", output.ToString(), StringComparison.Ordinal);
    }
}

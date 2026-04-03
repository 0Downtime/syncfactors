using SyncFactors.Domain;

namespace SyncFactors.Domain.Tests;

public sealed class LogSafetyTests
{
    [Fact]
    public void SingleLine_RemovesLineBreaksAndControlCharacters()
    {
        var result = LogSafety.SingleLine("worker-10001\r\nnext\u0001value");

        Assert.Equal("worker-10001 next value", result);
    }

    [Fact]
    public void SingleLine_PreservesNormalIdentifiers()
    {
        var result = LogSafety.SingleLine("personIdExternal");

        Assert.Equal("personIdExternal", result);
    }
}

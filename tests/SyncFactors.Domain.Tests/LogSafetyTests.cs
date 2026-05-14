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

    [Fact]
    public void RedactPii_RemovesDirectoryAndWorkerIdentifiers()
    {
        var result = LogSafety.RedactPii(
            "WorkerId=10001 SamAccountName=jdoe DistinguishedName=CN=Jane Doe,OU=Users,DC=example,DC=local UserPrincipalName=jane.doe@example.local");

        Assert.Contains("WorkerId=[REDACTED:WorkerId]", result, StringComparison.Ordinal);
        Assert.Contains("SamAccountName=[REDACTED:SamAccountName]", result, StringComparison.Ordinal);
        Assert.Contains("DistinguishedName=[REDACTED:DistinguishedName]", result, StringComparison.Ordinal);
        Assert.Contains("UserPrincipalName=[REDACTED:UserPrincipalName]", result, StringComparison.Ordinal);
        Assert.DoesNotContain("10001", result, StringComparison.Ordinal);
        Assert.DoesNotContain("jdoe", result, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Jane Doe", result, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("jane.doe@example.local", result, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void RedactPii_RedactsSuccessFactorsJsonBodyPreview()
    {
        var result = LogSafety.RedactPii(
            "BodyPreview={\"personIdExternal\":\"10001\",\"firstName\":\"Jane\",\"lastName\":\"Doe\",\"emailAddress\":\"jane.doe@example.local\"}");

        Assert.Contains("BodyPreview=[REDACTED:BodyPreview]", result, StringComparison.Ordinal);
        Assert.DoesNotContain("10001", result, StringComparison.Ordinal);
        Assert.DoesNotContain("Jane", result, StringComparison.Ordinal);
        Assert.DoesNotContain("jane.doe@example.local", result, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void RedactPii_DoesNotRedactNormalWorkerWords()
    {
        var result = LogSafety.RedactPii("Planned worker action. Bucket=updates AutoApply=True");

        Assert.Contains("Planned worker action.", result, StringComparison.Ordinal);
        Assert.Contains("Bucket=updates", result, StringComparison.Ordinal);
    }

    [Fact]
    public void RedactStructuredValue_RedactsSensitiveDictionaryEntries()
    {
        var value = new Dictionary<string, object?>
        {
            ["RequestedBy"] = "jane.doe@example.local",
            ["DryRun"] = true,
            ["Nested"] = new Dictionary<string, object?> { ["WorkerId"] = "10001" }
        };

        var result = Assert.IsType<Dictionary<string, object?>>(LogSafety.RedactStructuredValue(value));
        var nested = Assert.IsType<Dictionary<string, object?>>(result["Nested"]);

        Assert.Equal("[REDACTED:RequestedBy]", result["RequestedBy"]);
        Assert.True(result["DryRun"] is true);
        Assert.Equal("[REDACTED:WorkerId]", nested["WorkerId"]);
    }
}

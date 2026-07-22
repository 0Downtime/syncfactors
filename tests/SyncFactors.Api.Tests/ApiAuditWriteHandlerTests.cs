using SyncFactors.Api;

namespace SyncFactors.Api.Tests;

public sealed class ApiAuditWriteHandlerTests
{
    [Fact]
    public void TryWrite_WhenAuditPersistenceFails_ReturnsGenericFailureWithoutLeakingExceptionDetails()
    {
        var result = ApiAuditWriteHandler.TryWrite(() => throw new UnauthorizedAccessException("/secret/audit-path"));

        Assert.False(result);
        Assert.Equal("The action completed, but security audit recording failed.", ApiAuditWriteHandler.FailureMessage);
        Assert.DoesNotContain("/secret/audit-path", ApiAuditWriteHandler.FailureMessage, StringComparison.Ordinal);
    }

    [Fact]
    public void CreateFailureResponse_ExplicitlyReportsCompletedMutationWithoutLeakingAuditDetails()
    {
        var response = ApiAuditWriteHandler.CreateFailureResponse();

        Assert.True(response.ActionCompleted);
        Assert.False(response.AuditRecorded);
        Assert.Equal(ApiAuditWriteHandler.FailureMessage, response.Warning);
        Assert.DoesNotContain("audit-path", response.Warning, StringComparison.Ordinal);
    }
}

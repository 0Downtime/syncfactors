using Microsoft.Extensions.Logging;

namespace SyncFactors.Domain;

public static class RunLoggingScope
{
    public static IDisposable Begin(ILogger logger, string runId, string mode, string? requestId = null)
    {
        var scopeValues = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["RunId"] = runId,
            ["RunMode"] = mode
        };

        if (!string.IsNullOrWhiteSpace(requestId))
        {
            scopeValues["RequestId"] = requestId;
        }

        return logger.BeginScope(scopeValues) ?? NullDisposable.Instance;
    }

    private sealed class NullDisposable : IDisposable
    {
        public static readonly NullDisposable Instance = new();

        public void Dispose()
        {
        }
    }
}

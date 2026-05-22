using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace SyncFactors.Infrastructure;

public sealed class RunScopedFileLoggerProvider(
    string? configuredDirectory,
    int retainedFileCountLimit = LocalFileLogging.RunRetainedFileCountLimit) : RedactingFileLoggerProvider
{
    private readonly ConcurrentDictionary<string, byte> _prunedRunPaths = new(StringComparer.OrdinalIgnoreCase);

    protected override void Write(LogLevel logLevel, string categoryName, EventId eventId, string message, Exception? exception)
    {
        if (!TryGetRunId(out var runId))
        {
            return;
        }

        WriteToFile(
            runId,
            LocalFileLogging.ResolveRunLogPath(runId, configuredDirectory),
            DateTimeOffset.Now,
            logLevel,
            categoryName,
            eventId,
            message,
            exception);
        if (_prunedRunPaths.TryAdd(runId, 0))
        {
            LocalFileLogging.PruneRunLogFiles(configuredDirectory, retainedFileCountLimit);
        }
    }

    private bool TryGetRunId(out string runId)
    {
        var scopeState = new ScopeSearchState();
        ScopeProvider.ForEachScope(
            static (scope, state) =>
            {
                if (state.RunId is not null)
                {
                    return;
                }

                state.RunId = TryReadRunId(scope);
            },
            scopeState);

        var resolvedRunId = scopeState.RunId;
        if (string.IsNullOrWhiteSpace(resolvedRunId))
        {
            runId = string.Empty;
            return false;
        }

        runId = resolvedRunId;
        return true;
    }

    private static string? TryReadRunId(object? scope)
    {
        if (scope is IEnumerable<KeyValuePair<string, object?>> nullablePairs)
        {
            foreach (var pair in nullablePairs)
            {
                if (string.Equals(pair.Key, "RunId", StringComparison.OrdinalIgnoreCase))
                {
                    return pair.Value?.ToString();
                }
            }

            return null;
        }

        if (scope is IEnumerable<KeyValuePair<string, object>> pairs)
        {
            foreach (var pair in pairs)
            {
                if (string.Equals(pair.Key, "RunId", StringComparison.OrdinalIgnoreCase))
                {
                    return pair.Value?.ToString();
                }
            }
        }

        return null;
    }

    private sealed class ScopeSearchState
    {
        public string? RunId { get; set; }
    }
}

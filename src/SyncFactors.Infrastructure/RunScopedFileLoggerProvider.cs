using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace SyncFactors.Infrastructure;

public sealed class RunScopedFileLoggerProvider(string? configuredDirectory) : ILoggerProvider, ISupportExternalScope
{
    private readonly ConcurrentDictionary<string, RedactingLogFileWriter> _writers = new(StringComparer.OrdinalIgnoreCase);
    private IExternalScopeProvider _scopeProvider = new LoggerExternalScopeProvider();

    public ILogger CreateLogger(string categoryName)
    {
        return new RunScopedFileLogger(categoryName, this);
    }

    public void Dispose()
    {
        foreach (var writer in _writers.Values)
        {
            writer.Dispose();
        }

        _writers.Clear();
    }

    public void SetScopeProvider(IExternalScopeProvider scopeProvider)
    {
        _scopeProvider = scopeProvider ?? new LoggerExternalScopeProvider();
    }

    private void Write(LogLevel logLevel, string categoryName, EventId eventId, string message, Exception? exception)
    {
        if (!TryGetRunId(out var runId))
        {
            return;
        }

        var writer = _writers.GetOrAdd(
            runId,
            static (id, directory) => new RedactingLogFileWriter(LocalFileLogging.ResolveRunLogPath(id, directory)),
            configuredDirectory);

        writer.Write(DateTimeOffset.Now, logLevel, categoryName, eventId, message, exception);
    }

    private bool TryGetRunId(out string runId)
    {
        var scopeState = new ScopeSearchState();
        _scopeProvider.ForEachScope(
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

    private sealed class RunScopedFileLogger(string categoryName, RunScopedFileLoggerProvider provider) : ILogger
    {
        public IDisposable BeginScope<TState>(TState state) where TState : notnull
        {
            return provider._scopeProvider.Push(state);
        }

        public bool IsEnabled(LogLevel logLevel)
        {
            return logLevel != LogLevel.None;
        }

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel))
            {
                return;
            }

            var message = formatter(state, exception);
            if (string.IsNullOrWhiteSpace(message) && exception is null)
            {
                return;
            }

            provider.Write(logLevel, categoryName, eventId, message, exception);
        }
    }

    private sealed class ScopeSearchState
    {
        public string? RunId { get; set; }
    }
}

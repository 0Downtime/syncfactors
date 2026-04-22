using System.Collections.Concurrent;
using System.Globalization;
using Microsoft.Extensions.Logging;

namespace SyncFactors.Infrastructure;

public sealed class RunScopedFileLoggerProvider(string? configuredDirectory) : ILoggerProvider, ISupportExternalScope
{
    private readonly ConcurrentDictionary<string, RunLogWriter> _writers = new(StringComparer.OrdinalIgnoreCase);
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
            static (id, directory) => new RunLogWriter(LocalFileLogging.ResolveRunLogPath(id, directory)),
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

    private sealed class RunLogWriter(string path) : IDisposable
    {
        private readonly object _gate = new();
        private readonly StreamWriter _writer = CreateWriter(path);

        public void Dispose()
        {
            lock (_gate)
            {
                _writer.Dispose();
            }
        }

        public void Write(
            DateTimeOffset timestamp,
            LogLevel logLevel,
            string categoryName,
            EventId eventId,
            string message,
            Exception? exception)
        {
            lock (_gate)
            {
                _writer.Write(timestamp.ToString("yyyy-MM-dd HH:mm:ss.fff zzz", CultureInfo.InvariantCulture));
                _writer.Write(" [");
                _writer.Write(GetLevelCode(logLevel));
                _writer.Write("] ");
                _writer.Write(categoryName);

                if (eventId.Id != 0 || !string.IsNullOrWhiteSpace(eventId.Name))
                {
                    _writer.Write(" (EventId=");
                    _writer.Write(eventId.Id.ToString(CultureInfo.InvariantCulture));
                    if (!string.IsNullOrWhiteSpace(eventId.Name))
                    {
                        _writer.Write(":");
                        _writer.Write(eventId.Name);
                    }

                    _writer.Write(")");
                }

                _writer.Write(": ");
                _writer.WriteLine(message);
                if (exception is not null)
                {
                    _writer.WriteLine(exception);
                }
            }
        }

        private static StreamWriter CreateWriter(string path)
        {
            var directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrWhiteSpace(directory))
            {
                Directory.CreateDirectory(directory);
            }

            return new StreamWriter(new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite))
            {
                AutoFlush = true
            };
        }

        private static string GetLevelCode(LogLevel logLevel)
        {
            return logLevel switch
            {
                LogLevel.Trace => "TRC",
                LogLevel.Debug => "DBG",
                LogLevel.Information => "INF",
                LogLevel.Warning => "WRN",
                LogLevel.Error => "ERR",
                LogLevel.Critical => "CRT",
                _ => "NON"
            };
        }
    }

    private sealed class ScopeSearchState
    {
        public string? RunId { get; set; }
    }
}

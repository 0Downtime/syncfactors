using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace SyncFactors.Infrastructure;

public sealed class LocalFileLoggerProvider(string processName, string? configuredDirectory) : ILoggerProvider, ISupportExternalScope
{
    private readonly ConcurrentDictionary<string, RedactingLogFileWriter> _writers = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, byte> _prunedPaths = new(StringComparer.OrdinalIgnoreCase);
    private IExternalScopeProvider _scopeProvider = new LoggerExternalScopeProvider();

    public ILogger CreateLogger(string categoryName)
    {
        return new LocalFileLogger(categoryName, this);
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
        var timestamp = DateTimeOffset.Now;
        var path = LocalFileLogging.ResolveDatedFilePath(processName, configuredDirectory, timestamp);
        if (_prunedPaths.TryAdd(path, 0))
        {
            LocalFileLogging.PruneDatedFiles(processName, configuredDirectory);
        }

        var writer = _writers.GetOrAdd(path, static resolvedPath => new RedactingLogFileWriter(resolvedPath));
        writer.Write(timestamp, logLevel, categoryName, eventId, message, exception);
    }

    private sealed class LocalFileLogger(string categoryName, LocalFileLoggerProvider provider) : ILogger
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

}

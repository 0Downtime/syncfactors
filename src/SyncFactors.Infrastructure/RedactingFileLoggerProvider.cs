using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace SyncFactors.Infrastructure;

public abstract class RedactingFileLoggerProvider : ILoggerProvider, ISupportExternalScope
{
    private readonly ConcurrentDictionary<string, RedactingLogFileWriter> _writers = new(StringComparer.OrdinalIgnoreCase);
    private IExternalScopeProvider _scopeProvider = new LoggerExternalScopeProvider();

    protected IExternalScopeProvider ScopeProvider => _scopeProvider;

    public ILogger CreateLogger(string categoryName)
    {
        return new RedactingFileLogger(categoryName, this);
    }

    public void Dispose()
    {
        Dispose(disposing: true);
        GC.SuppressFinalize(this);
    }

    protected virtual void Dispose(bool disposing)
    {
        if (!disposing)
        {
            return;
        }

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

    protected abstract void Write(LogLevel logLevel, string categoryName, EventId eventId, string message, Exception? exception);

    protected void WriteToFile(
        string writerKey,
        string path,
        DateTimeOffset timestamp,
        LogLevel logLevel,
        string categoryName,
        EventId eventId,
        string message,
        Exception? exception)
    {
        var writer = _writers.GetOrAdd(writerKey, _ => new RedactingLogFileWriter(path));
        writer.Write(timestamp, logLevel, categoryName, eventId, message, exception);
    }

    private sealed class RedactingFileLogger(string categoryName, RedactingFileLoggerProvider provider) : ILogger
    {
        public IDisposable BeginScope<TState>(TState state) where TState : notnull
        {
            return provider.ScopeProvider.Push(state);
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

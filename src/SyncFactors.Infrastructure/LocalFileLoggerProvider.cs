using System.Collections.Concurrent;
using System.Globalization;
using Microsoft.Extensions.Logging;
using SyncFactors.Domain;

namespace SyncFactors.Infrastructure;

public sealed class LocalFileLoggerProvider(string processName, string? configuredDirectory) : ILoggerProvider, ISupportExternalScope
{
    private readonly ConcurrentDictionary<string, DailyLogWriter> _writers = new(StringComparer.OrdinalIgnoreCase);
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

        var writer = _writers.GetOrAdd(path, static resolvedPath => new DailyLogWriter(resolvedPath));
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

    private sealed class DailyLogWriter(string path) : IDisposable
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
                        _writer.Write(LogSafety.RedactPiiInText(eventId.Name));
                    }

                    _writer.Write(")");
                }

                _writer.Write(": ");
                _writer.WriteLine(LogSafety.RedactPii(message));
                if (exception is not null)
                {
                    _writer.WriteLine(LogSafety.RedactPiiInText(exception.ToString()));
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
}

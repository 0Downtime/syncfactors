using System.Globalization;
using Microsoft.Extensions.Logging;
using SyncFactors.Domain;

namespace SyncFactors.Infrastructure;

internal sealed class RedactingLogFileWriter(string path) : IDisposable
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

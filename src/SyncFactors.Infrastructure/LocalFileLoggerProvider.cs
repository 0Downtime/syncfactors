using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace SyncFactors.Infrastructure;

public sealed class LocalFileLoggerProvider(string processName, string? configuredDirectory) : RedactingFileLoggerProvider
{
    private readonly ConcurrentDictionary<string, byte> _prunedPaths = new(StringComparer.OrdinalIgnoreCase);

    protected override void Write(LogLevel logLevel, string categoryName, EventId eventId, string message, Exception? exception)
    {
        var timestamp = DateTimeOffset.Now;
        var path = LocalFileLogging.ResolveDatedFilePath(processName, configuredDirectory, timestamp);
        if (_prunedPaths.TryAdd(path, 0))
        {
            LocalFileLogging.PruneDatedFiles(processName, configuredDirectory);
        }

        WriteToFile(path, path, timestamp, logLevel, categoryName, eventId, message, exception);
    }
}

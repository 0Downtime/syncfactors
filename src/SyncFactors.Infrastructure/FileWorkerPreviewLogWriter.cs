using System.Text.Json;
using SyncFactors.Contracts;
using SyncFactors.Domain;

namespace SyncFactors.Infrastructure;

public sealed class FileWorkerPreviewLogWriter : IWorkerPreviewLogWriter
{
    public string CreateLogPath(string workerId, DateTimeOffset startedAt)
    {
        var previewDirectory = LocalFileLogging.ResolvePreviewLogDirectory(
            Environment.GetEnvironmentVariable(LocalFileLogging.DirectoryEnvironmentVariable));
        Directory.CreateDirectory(previewDirectory);

        var safeWorkerId = MakeSafeFileName(workerId);
        var suffix = Guid.NewGuid().ToString("N")[..8];
        return Path.Combine(previewDirectory, $"preview-{safeWorkerId}-{startedAt:yyyyMMddHHmmssfff}-{suffix}.jsonl");
    }

    public async Task AppendAsync(string logPath, WorkerPreviewLogEntry entry, CancellationToken cancellationToken)
    {
        var directory = Path.GetDirectoryName(logPath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var line = JsonSerializer.Serialize(entry, JsonOptions.Default);
        await File.AppendAllTextAsync(logPath, line + Environment.NewLine, cancellationToken);
    }

    private static string MakeSafeFileName(string value)
    {
        var invalidChars = Path.GetInvalidFileNameChars();
        return new string(value.Select(ch => invalidChars.Contains(ch) ? '_' : ch).ToArray());
    }
}

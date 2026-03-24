using System.Diagnostics;
using System.Text.Json;

namespace SyncFactors.Infrastructure;

public sealed class SqliteJsonShell
{
    public async Task<IReadOnlyList<T>> QueryAsync<T>(string databasePath, string sql, CancellationToken cancellationToken)
    {
        if (!File.Exists(databasePath))
        {
            return [];
        }

        var startInfo = new ProcessStartInfo
        {
            FileName = "sqlite3",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };

        startInfo.ArgumentList.Add("-json");
        startInfo.ArgumentList.Add(databasePath);
        startInfo.ArgumentList.Add(sql);

        using var process = new Process { StartInfo = startInfo };
        process.Start();

        var stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);

        await process.WaitForExitAsync(cancellationToken);

        var stdout = await stdoutTask;
        var stderr = await stderrTask;

        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException($"sqlite3 query failed: {stderr}".Trim());
        }

        if (string.IsNullOrWhiteSpace(stdout))
        {
            return [];
        }

        return JsonSerializer.Deserialize<List<T>>(stdout, JsonOptions.Default) ?? [];
    }
}

using SyncFactors.Contracts;

namespace SyncFactors.Worker;

public sealed class WorkerDeploymentCommitGate
{
    public const string ConfigurationKey = "SyncFactors:Deployment:CommitMarkerPath";
    public const string DefaultMarkerSuffix = ".deployment-commit";

    private static readonly TimeSpan DefaultPollingInterval = TimeSpan.FromSeconds(1);
    private readonly string? _expectedNonceHash;
    private readonly string _markerPath;
    private readonly TimeSpan _pollingInterval;

    public WorkerDeploymentCommitGate(
        string? expectedNonceHash,
        string markerPath,
        TimeSpan? pollingInterval = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(markerPath);

        _expectedNonceHash = expectedNonceHash;
        _markerPath = Path.GetFullPath(markerPath);
        _pollingInterval = pollingInterval ?? DefaultPollingInterval;
        if (_pollingInterval <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(pollingInterval), "Polling interval must be positive.");
        }
    }

    public bool IsRequired => !string.IsNullOrWhiteSpace(_expectedNonceHash);

    public static string ResolveMarkerPath(string? configuredPath, string sqliteDatabasePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sqliteDatabasePath);

        if (!string.IsNullOrWhiteSpace(configuredPath))
        {
            return Path.GetFullPath(Environment.ExpandEnvironmentVariables(configuredPath.Trim()));
        }

        return Path.GetFullPath(sqliteDatabasePath) + DefaultMarkerSuffix;
    }

    public async Task WaitForCommitAsync(CancellationToken cancellationToken)
    {
        if (!IsRequired)
        {
            return;
        }

        while (!await HasValidMarkerAsync(cancellationToken))
        {
            await Task.Delay(_pollingInterval, cancellationToken);
        }
    }

    private async Task<bool> HasValidMarkerAsync(CancellationToken cancellationToken)
    {
        try
        {
            if (!File.Exists(_markerPath))
            {
                return false;
            }

            var candidateHash = await File.ReadAllTextAsync(_markerPath, cancellationToken);
            return WorkerDeploymentAttestation.HashesMatch(_expectedNonceHash, candidateHash);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }
}

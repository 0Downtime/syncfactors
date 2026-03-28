using Microsoft.Extensions.Logging.Abstractions;
using SyncFactors.Contracts;
using SyncFactors.Domain;
using SyncFactors.Infrastructure;

namespace SyncFactors.MockSuccessFactors.Tests;

public sealed class WorkerPreviewLogWriterTests
{
    [Fact]
    public async Task PreviewPlanner_WritesParseableJsonlLog_ToReportingDirectory()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "syncfactors-preview-log", Guid.NewGuid().ToString("N"));
        var configPath = Path.Combine(tempRoot, "sync-config.json");
        var mappingConfigPath = Path.Combine(tempRoot, "mapping-config.json");
        var scaffoldDataPath = Path.Combine(tempRoot, "scaffold-data.json");
        var reportingDirectory = Path.Combine(tempRoot, "reports");

        Directory.CreateDirectory(tempRoot);
        await File.WriteAllTextAsync(configPath, """
        {
          "secrets": {
            "adServerEnv": null,
            "adUsernameEnv": null,
            "adBindPasswordEnv": null
          },
          "successFactors": {
            "baseUrl": "http://example.test/odata/v2",
            "auth": {
              "mode": "basic",
              "basic": {
                "username": "mock-user",
                "password": "mock-password"
              }
            },
            "query": {
              "entitySet": "PerPerson",
              "identityField": "personIdExternal",
              "deltaField": "lastModifiedDateTime",
              "select": ["personIdExternal", "personalInfoNav/firstName", "personalInfoNav/lastName"],
              "expand": ["personalInfoNav"]
            }
          },
          "ad": {
            "server": "ldap.example.test",
            "username": "",
            "bindPassword": "",
            "identityAttribute": "employeeID",
            "defaultActiveOu": "OU=LabUsers,DC=example,DC=com",
            "graveyardOu": "OU=LabGraveyard,DC=example,DC=com"
          },
          "sync": {
            "enableBeforeStartDays": 7,
            "deletionRetentionDays": 90
          },
          "safety": {
            "maxCreatesPerRun": 10,
            "maxDisablesPerRun": 10,
            "maxDeletionsPerRun": 10
          },
          "reporting": {
            "outputDirectory": ""
          }
        }
        """);
        File.Copy(Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "config", "sample.empjob-confirmed.mapping-config.json")), mappingConfigPath);
        await File.WriteAllTextAsync(scaffoldDataPath, """
        {
          "workers": [
            {
              "workerId": "1000123",
              "preferredName": "Bootstrap",
              "lastName": "Worker123",
              "department": "Platform",
              "targetOu": "OU=Bootstrap,DC=example,DC=com",
              "isPrehire": false
            }
          ],
          "directoryUsers": []
        }
        """);

        var configJson = await File.ReadAllTextAsync(configPath);
        configJson = configJson.Replace("\"outputDirectory\": \"\"", $"\"outputDirectory\": \"{reportingDirectory.Replace("\\", "\\\\")}\"", StringComparison.Ordinal);
        await File.WriteAllTextAsync(configPath, configJson);

        var loader = new SyncFactorsConfigurationLoader(new SyncFactorsConfigPathResolver(configPath, mappingConfigPath));
        var scaffoldStore = new ScaffoldDataStore(new ScaffoldDataPathResolver(scaffoldDataPath));
        var logWriter = new FileWorkerPreviewLogWriter(loader);
        var mappingProvider = new AttributeMappingProvider(loader, NullLogger<AttributeMappingProvider>.Instance);
        var diffService = new AttributeDiffService(mappingProvider, logWriter, NullLogger<AttributeDiffService>.Instance);
        var workerSource = new ScaffoldWorkerSource(scaffoldStore);
        var directoryGateway = new ScaffoldDirectoryGateway(scaffoldStore);
        var planner = new WorkerPreviewPlanner(
            workerSource,
            directoryGateway,
            new IdentityMatcher(),
            diffService,
            mappingProvider,
            logWriter,
            new StubRunRepository(),
            NullLogger<WorkerPreviewPlanner>.Instance);

        var preview = await planner.PreviewAsync("1000123", CancellationToken.None);

        Assert.NotNull(preview.ReportPath);
        Assert.EndsWith(".jsonl", preview.ReportPath);
        Assert.True(File.Exists(preview.ReportPath));

        var lines = await File.ReadAllLinesAsync(preview.ReportPath);
        Assert.Contains(lines, line => line.Contains("\"Event\":\"preview.diff.start\"", StringComparison.Ordinal));
        Assert.Contains(lines, line => line.Contains("\"Event\":\"preview.diff.mapping\"", StringComparison.Ordinal));
        Assert.Contains(lines, line => line.Contains("\"Event\":\"preview.diff.complete\"", StringComparison.Ordinal));
    }

    private sealed class StubRunRepository : IRunRepository
    {
        public Task<IReadOnlyList<RunSummary>> ListRunsAsync(CancellationToken cancellationToken)
        {
            _ = cancellationToken;
            return Task.FromResult<IReadOnlyList<RunSummary>>([]);
        }

        public Task<RunDetail?> GetRunAsync(string runId, CancellationToken cancellationToken)
        {
            _ = runId;
            _ = cancellationToken;
            return Task.FromResult<RunDetail?>(null);
        }

        public Task<WorkerPreviewResult?> GetWorkerPreviewAsync(string runId, CancellationToken cancellationToken)
        {
            _ = runId;
            _ = cancellationToken;
            return Task.FromResult<WorkerPreviewResult?>(null);
        }

        public Task<IReadOnlyList<WorkerPreviewHistoryItem>> ListWorkerPreviewHistoryAsync(string workerId, int take, CancellationToken cancellationToken)
        {
            _ = workerId;
            _ = take;
            _ = cancellationToken;
            return Task.FromResult<IReadOnlyList<WorkerPreviewHistoryItem>>([]);
        }

        public Task SaveRunAsync(RunRecord run, CancellationToken cancellationToken)
        {
            _ = run;
            _ = cancellationToken;
            return Task.CompletedTask;
        }

        public Task ReplaceRunEntriesAsync(string runId, IReadOnlyList<RunEntryRecord> entries, CancellationToken cancellationToken)
        {
            _ = runId;
            _ = entries;
            _ = cancellationToken;
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<RunEntry>> GetRunEntriesAsync(string runId, string? bucket, string? workerId, string? reason, string? filter, string? entryId, CancellationToken cancellationToken)
        {
            _ = runId;
            _ = bucket;
            _ = workerId;
            _ = reason;
            _ = filter;
            _ = entryId;
            _ = cancellationToken;
            return Task.FromResult<IReadOnlyList<RunEntry>>([]);
        }
    }
}

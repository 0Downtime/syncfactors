using Microsoft.Extensions.Logging.Abstractions;
using SyncFactors.Domain;
using SyncFactors.Infrastructure;

namespace SyncFactors.Infrastructure.Tests;

public sealed class SuccessFactorsDeltaSyncServiceTests
{
    [Fact]
    public async Task GetWindowAsync_WithCheckpoint_ReturnsFormattedDeltaFilter()
    {
        var tempDirectory = Path.Combine(Path.GetTempPath(), "syncfactors-delta-config", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectory);

        try
        {
            var syncConfigPath = Path.Combine(tempDirectory, "sync-config.json");
            var mappingConfigPath = Path.Combine(tempDirectory, "mapping-config.json");

            await File.WriteAllTextAsync(syncConfigPath, """
            {
              "secrets": {
                "adServerEnv": null,
                "adUsernameEnv": null,
                "adBindPasswordEnv": null
              },
              "successFactors": {
                "baseUrl": "https://example.successfactors.com/odata/v2",
                "auth": {
                  "mode": "basic",
                  "basic": {
                    "username": "user",
                    "password": "pass"
                  }
                },
                "query": {
                  "entitySet": "EmpJob",
                  "identityField": "userId",
                  "deltaField": "lastModifiedDateTime",
                  "deltaSyncEnabled": true,
                  "deltaOverlapMinutes": 5,
                  "baseFilter": "emplStatus in 'A','U'",
                  "pageSize": 200,
                  "select": [ "userId" ],
                  "expand": []
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
                "outputDirectory": "reports"
              }
            }
            """);
            await File.WriteAllTextAsync(mappingConfigPath, """{"mappings":[{"source":"userId","target":"employeeID","enabled":true,"required":true,"transform":"copy"}]}""");

            var configLoader = new SyncFactorsConfigurationLoader(new SyncFactorsConfigPathResolver(syncConfigPath, mappingConfigPath));
            var stateStore = new StubDeltaSyncStateStore(DateTimeOffset.Parse("2026-03-31T10:15:00Z"));
            var service = new SuccessFactorsDeltaSyncService(configLoader, stateStore, TimeProvider.System, NullLogger<SuccessFactorsDeltaSyncService>.Instance);

            var window = await service.GetWindowAsync(CancellationToken.None);

            Assert.True(window.Enabled);
            Assert.True(window.HasCheckpoint);
            Assert.Equal(DateTimeOffset.Parse("2026-03-31T10:15:00Z"), window.CheckpointUtc);
            Assert.Equal(DateTimeOffset.Parse("2026-03-31T10:10:00Z"), window.EffectiveSinceUtc);
            Assert.Equal("lastModifiedDateTime ge datetimeoffset'2026-03-31T10:10:00Z'", window.Filter);
        }
        finally
        {
            Directory.Delete(tempDirectory, recursive: true);
        }
    }

    private sealed class StubDeltaSyncStateStore(DateTimeOffset? checkpointUtc) : IDeltaSyncStateStore
    {
        public Task<DateTimeOffset?> GetCheckpointAsync(string syncKey, CancellationToken cancellationToken)
        {
            _ = syncKey;
            _ = cancellationToken;
            return Task.FromResult(checkpointUtc);
        }

        public Task SaveCheckpointAsync(string syncKey, DateTimeOffset checkpointUtc, CancellationToken cancellationToken)
        {
            _ = syncKey;
            _ = checkpointUtc;
            _ = cancellationToken;
            return Task.CompletedTask;
        }
    }
}

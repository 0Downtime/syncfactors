using Microsoft.Extensions.Logging.Abstractions;
using SyncFactors.Domain;
using SyncFactors.Infrastructure;

namespace SyncFactors.Infrastructure.Tests;

public sealed class SuccessFactorsDeltaSyncServiceTests
{
    [Fact]
    public async Task GetWindowAsync_WithCheckpoint_ReturnsFormattedDeltaFilter()
    {
        var tempDirectory = CreateTempDirectory();

        try
        {
            var (syncConfigPath, mappingConfigPath) = await WriteConfigFilesAsync(
                tempDirectory,
                queryJson: """
                {
                  "entitySet": "EmpJob",
                  "identityField": "userId",
                  "deltaField": "lastModifiedDateTime",
                  "onboardingDateField": "startDate",
                  "deltaSyncEnabled": true,
                  "deltaOverlapMinutes": 5,
                  "baseFilter": "emplStatus in 'A','U'",
                  "pageSize": 200,
                  "select": [ "userId" ],
                  "expand": []
                }
                """);

            var configLoader = new SyncFactorsConfigurationLoader(new SyncFactorsConfigPathResolver(syncConfigPath, mappingConfigPath));
            var stateStore = new StubDeltaSyncStateStore(DateTimeOffset.Parse("2026-03-31T10:15:00Z"));
            var service = new SuccessFactorsDeltaSyncService(
                configLoader,
                stateStore,
                new FakeTimeProvider(DateTimeOffset.Parse("2026-04-02T12:00:00Z")),
                NullLogger<SuccessFactorsDeltaSyncService>.Instance);

            var window = await service.GetWindowAsync(CancellationToken.None);

            Assert.True(window.Enabled);
            Assert.True(window.HasCheckpoint);
            Assert.Equal(DateTimeOffset.Parse("2026-03-31T10:15:00Z"), window.CheckpointUtc);
            Assert.Equal(DateTimeOffset.Parse("2026-03-31T10:10:00Z"), window.EffectiveSinceUtc);
            Assert.Equal(
                "(lastModifiedDateTime ge datetimeoffset'2026-03-31T10:10:00Z') or " +
                "(startDate gt datetime'2026-03-31T00:00:00' and startDate le datetime'2026-04-02T00:00:00') or " +
                "(startDate gt datetime'2026-04-07T00:00:00' and startDate le datetime'2026-04-09T00:00:00')",
                window.Filter);
        }
        finally
        {
            Directory.Delete(tempDirectory, recursive: true);
        }
    }

    [Fact]
    public async Task GetWindowAsync_WithPreviewBackedMappings_DisablesDelta()
    {
        var tempDirectory = CreateTempDirectory();

        try
        {
            var (syncConfigPath, mappingConfigPath) = await WriteConfigFilesAsync(
                tempDirectory,
                queryJson: """
                {
                  "entitySet": "EmpJob",
                  "identityField": "userId",
                  "deltaField": "lastModifiedDateTime",
                  "onboardingDateField": "startDate",
                  "deltaSyncEnabled": true,
                  "deltaOverlapMinutes": 5,
                  "pageSize": 200,
                  "select": [ "userId" ],
                  "expand": []
                }
                """,
                previewQueryJson: """
                {
                  "entitySet": "PerPerson",
                  "identityField": "personIdExternal",
                  "deltaField": "lastModifiedDateTime",
                  "pageSize": 200,
                  "select": [ "personIdExternal", "personalInfoNav/firstName" ],
                  "expand": [ "personalInfoNav" ]
                }
                """,
                mappingJson: """
                {
                  "mappings": [
                    {
                      "source": "personalInfoNav[0].firstName",
                      "target": "GivenName",
                      "enabled": true,
                      "required": true,
                      "transform": "Trim"
                    }
                  ]
                }
                """);

            var configLoader = new SyncFactorsConfigurationLoader(new SyncFactorsConfigPathResolver(syncConfigPath, mappingConfigPath));
            var stateStore = new StubDeltaSyncStateStore(DateTimeOffset.Parse("2026-03-31T10:15:00Z"));
            var service = new SuccessFactorsDeltaSyncService(
                configLoader,
                stateStore,
                new FakeTimeProvider(DateTimeOffset.Parse("2026-04-02T12:00:00Z")),
                NullLogger<SuccessFactorsDeltaSyncService>.Instance);

            var window = await service.GetWindowAsync(CancellationToken.None);

            Assert.False(window.Enabled);
            Assert.False(window.HasCheckpoint);
            Assert.Null(window.Filter);
        }
        finally
        {
            Directory.Delete(tempDirectory, recursive: true);
        }
    }

    [Fact]
    public async Task GetWindowAsync_UsesOnboardingFieldAndEnableBeforeStartDaysInSyncKey()
    {
        var tempDirectory = CreateTempDirectory();

        try
        {
            var (syncConfigPath, mappingConfigPath) = await WriteConfigFilesAsync(
                tempDirectory,
                queryJson: """
                {
                  "entitySet": "EmpJob",
                  "identityField": "userId",
                  "deltaField": "lastModifiedDateTime",
                  "onboardingDateField": "employmentNav/startDate",
                  "deltaSyncEnabled": true,
                  "deltaOverlapMinutes": 5,
                  "baseFilter": "emplStatus in 'A','U'",
                  "pageSize": 200,
                  "select": [ "userId" ],
                  "expand": []
                }
                """,
                syncJson: """
                {
                  "enableBeforeStartDays": 14,
                  "deletionRetentionDays": 90
                }
                """);

            var configLoader = new SyncFactorsConfigurationLoader(new SyncFactorsConfigPathResolver(syncConfigPath, mappingConfigPath));
            var stateStore = new StubDeltaSyncStateStore(DateTimeOffset.Parse("2026-03-31T10:15:00Z"));
            var service = new SuccessFactorsDeltaSyncService(
                configLoader,
                stateStore,
                new FakeTimeProvider(DateTimeOffset.Parse("2026-04-02T12:00:00Z")),
                NullLogger<SuccessFactorsDeltaSyncService>.Instance);

            _ = await service.GetWindowAsync(CancellationToken.None);

            Assert.Equal(
                "EmpJob|userId|lastModifiedDateTime|employmentNav/startDate|emplStatus in 'A','U'||14",
                stateStore.LastRequestedSyncKey);
        }
        finally
        {
            Directory.Delete(tempDirectory, recursive: true);
        }
    }

    [Fact]
    public async Task RecordSuccessfulRunAsync_UsesSuppliedCheckpointUtc()
    {
        var tempDirectory = CreateTempDirectory();

        try
        {
            var (syncConfigPath, mappingConfigPath) = await WriteConfigFilesAsync(
                tempDirectory,
                queryJson: """
                {
                  "entitySet": "EmpJob",
                  "identityField": "userId",
                  "deltaField": "lastModifiedDateTime",
                  "onboardingDateField": "startDate",
                  "deltaSyncEnabled": true,
                  "deltaOverlapMinutes": 5,
                  "pageSize": 200,
                  "select": [ "userId" ],
                  "expand": []
                }
                """);

            var configLoader = new SyncFactorsConfigurationLoader(new SyncFactorsConfigPathResolver(syncConfigPath, mappingConfigPath));
            var stateStore = new StubDeltaSyncStateStore(null);
            var service = new SuccessFactorsDeltaSyncService(
                configLoader,
                stateStore,
                new FakeTimeProvider(DateTimeOffset.Parse("2026-04-14T12:05:00Z")),
                NullLogger<SuccessFactorsDeltaSyncService>.Instance);
            var checkpointUtc = DateTimeOffset.Parse("2026-04-14T12:00:00Z");

            await service.RecordSuccessfulRunAsync(checkpointUtc, CancellationToken.None);

            Assert.Equal(checkpointUtc, stateStore.SavedCheckpointUtc);
            Assert.Equal("EmpJob|userId|lastModifiedDateTime|startDate|||7", stateStore.LastSavedSyncKey);
        }
        finally
        {
            Directory.Delete(tempDirectory, recursive: true);
        }
    }

    private static string CreateTempDirectory()
    {
        var tempDirectory = Path.Combine(Path.GetTempPath(), "syncfactors-delta-config", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectory);
        return tempDirectory;
    }

    private static async Task<(string SyncConfigPath, string MappingConfigPath)> WriteConfigFilesAsync(
        string tempDirectory,
        string queryJson,
        string? previewQueryJson = null,
        string? mappingJson = null,
        string? syncJson = null)
    {
        var syncConfigPath = Path.Combine(tempDirectory, "sync-config.json");
        var mappingConfigPath = Path.Combine(tempDirectory, "mapping-config.json");
        var renderedPreviewQuery = string.IsNullOrWhiteSpace(previewQueryJson)
            ? string.Empty
            : $",{Environment.NewLine}\"previewQuery\": {previewQueryJson}";
        var renderedSyncJson = string.IsNullOrWhiteSpace(syncJson)
            ? """
                {
                  "enableBeforeStartDays": 7,
                  "deletionRetentionDays": 90
                }
                """
            : syncJson;

        await File.WriteAllTextAsync(syncConfigPath, $$"""
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
            "query": {{queryJson}}{{renderedPreviewQuery}}
          },
          "ad": {
            "server": "ldap.example.test",
            "username": "",
            "bindPassword": "",
            "identityAttribute": "employeeID",
            "defaultActiveOu": "OU=LabUsers,DC=example,DC=com",
            "prehireOu": "OU=Prehire,DC=example,DC=com",
            "graveyardOu": "OU=LabGraveyard,DC=example,DC=com"
          },
          "sync": {{renderedSyncJson}},
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

        await File.WriteAllTextAsync(
            mappingConfigPath,
            mappingJson ?? """{"mappings":[{"source":"userId","target":"employeeID","enabled":true,"required":true,"transform":"copy"}]}""");

        return (syncConfigPath, mappingConfigPath);
    }

    private sealed class StubDeltaSyncStateStore(DateTimeOffset? checkpointUtc) : IDeltaSyncStateStore
    {
        public string? LastRequestedSyncKey { get; private set; }
        public string? LastSavedSyncKey { get; private set; }
        public DateTimeOffset? SavedCheckpointUtc { get; private set; }

        public Task<DateTimeOffset?> GetCheckpointAsync(string syncKey, CancellationToken cancellationToken)
        {
            LastRequestedSyncKey = syncKey;
            _ = cancellationToken;
            return Task.FromResult(checkpointUtc);
        }

        public Task SaveCheckpointAsync(string syncKey, DateTimeOffset checkpointUtc, CancellationToken cancellationToken)
        {
            LastSavedSyncKey = syncKey;
            SavedCheckpointUtc = checkpointUtc;
            _ = cancellationToken;
            return Task.CompletedTask;
        }
    }

    private sealed class FakeTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => utcNow;
    }
}

using Microsoft.Extensions.Logging.Abstractions;
using SyncFactors.Domain;
using SyncFactors.Infrastructure;
using System.Net;
using System.Net.Http;
using System.Text;

namespace SyncFactors.Infrastructure.Tests;

public sealed class SuccessFactorsWorkerSourcePreviewQueryTests
{
    [Fact]
    public async Task GetWorkerAsync_UsesPreviewQueryToResolveIdentity_AndMergesCanonicalSyncData()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "syncfactors-preview-query-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);

        try
        {
            var syncConfigPath = Path.Combine(tempRoot, "sync-config.json");
            var scaffoldDataPath = Path.Combine(tempRoot, "scaffold-data.json");

            await File.WriteAllTextAsync(
                syncConfigPath,
                """
                {
                  "secrets": {},
                  "ad": {
                    "server": "ldap.example.invalid",
                    "username": "",
                    "bindPassword": "",
                    "defaultActiveOu": "OU=LabUsers,DC=example,DC=com",
                    "prehireOu": "OU=Prehire,DC=example,DC=com",
                    "graveyardOu": "OU=Graveyard,DC=example,DC=com",
                    "identityAttribute": "employeeID"
                  },
                  "successFactors": {
                    "baseUrl": "http://sf.example/odata/v2",
                    "query": {
                      "entitySet": "EmpJob",
                      "identityField": "userId",
                      "deltaField": "lastModifiedDateTime",
                      "select": [ "userId", "jobTitle", "department" ],
                      "expand": []
                    },
                    "previewQuery": {
                      "entitySet": "PerPerson",
                      "identityField": "personIdExternal",
                      "deltaField": "lastModifiedDateTime",
                      "select": [
                        "personIdExternal",
                        "employmentNav/userNav/userId",
                        "personalInfoNav/firstName",
                        "personalInfoNav/lastName",
                        "employmentNav/jobInfoNav/departmentNav/name_localized"
                      ],
                      "expand": [
                        "personalInfoNav",
                        "employmentNav",
                        "employmentNav/userNav",
                        "employmentNav/jobInfoNav",
                        "employmentNav/jobInfoNav/departmentNav"
                      ]
                    },
                    "auth": {
                      "mode": "basic",
                      "basic": {
                        "username": "user",
                        "password": "pass"
                      }
                    }
                  },
                  "sync": {
                    "enableBeforeStartDays": 7,
                    "deletionRetentionDays": 30
                  },
                  "safety": {
                    "maxCreatesPerRun": 25,
                    "maxDisablesPerRun": 25,
                    "maxDeletionsPerRun": 25
                  },
                  "reporting": {
                    "outputDirectory": "/tmp"
                  }
                }
                """);

            await File.WriteAllTextAsync(
                scaffoldDataPath,
                """
                {
                  "workers": [],
                  "directoryUsers": []
                }
                """);

            var handler = new PreviewOnlyMessageHandler();
            var source = new SuccessFactorsWorkerSource(
                new HttpClient(handler),
                new SyncFactorsConfigurationLoader(new SyncFactorsConfigPathResolver(syncConfigPath, null)),
                new StubDeltaSyncService(),
                new ScaffoldWorkerSource(new ScaffoldDataStore(new ScaffoldDataPathResolver(scaffoldDataPath))),
                NullLogger<SuccessFactorsWorkerSource>.Instance);

            var worker = await source.GetWorkerAsync("10000", CancellationToken.None);

            Assert.NotNull(worker);
            Assert.Equal("10000", worker.WorkerId);
            Assert.Equal("Ada", worker.PreferredName);
            Assert.Equal("Lovelace", worker.LastName);
            Assert.Equal("Platform", worker.Department);
            Assert.Equal("Engineer", worker.Attributes["jobTitle"]);
            Assert.Contains(handler.RequestUris, uri => uri.Contains("/PerPerson?", StringComparison.Ordinal));
            Assert.Contains(handler.RequestUris, uri => uri.Contains("/EmpJob?", StringComparison.Ordinal) && uri.Contains("U10000", StringComparison.Ordinal));
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    private sealed class StubDeltaSyncService : IDeltaSyncService
    {
        public Task<DeltaSyncWindow> GetWindowAsync(CancellationToken cancellationToken)
        {
            _ = cancellationToken;
            return Task.FromResult(new DeltaSyncWindow(
                Enabled: false,
                HasCheckpoint: false,
                Filter: null,
                DeltaField: "lastModifiedDateTime",
                CheckpointUtc: null,
                EffectiveSinceUtc: null));
        }

        public Task RecordSuccessfulRunAsync(DateTimeOffset checkpointUtc, CancellationToken cancellationToken)
        {
            _ = checkpointUtc;
            _ = cancellationToken;
            return Task.CompletedTask;
        }
    }

    private sealed class PreviewOnlyMessageHandler : HttpMessageHandler
    {
        public List<string> RequestUris { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            _ = cancellationToken;
            RequestUris.Add(request.RequestUri?.ToString() ?? string.Empty);

            if (request.RequestUri is not null &&
                request.RequestUri.AbsoluteUri.Contains("/PerPerson?", StringComparison.Ordinal))
            {
                return Task.FromResult(JsonResponse(
                    """
                    {
                      "d": {
                        "results": [
                          {
                            "personIdExternal": "10000",
                            "employmentNav": {
                              "results": [
                                {
                                  "userNav": {
                                    "userId": "U10000"
                                  },
                                  "jobInfoNav": {
                                    "results": [
                                      {
                                        "departmentNav": {
                                          "name_localized": "Platform"
                                        }
                                      }
                                    ]
                                  }
                                }
                              ]
                            },
                            "personalInfoNav": {
                              "results": [
                                {
                                  "firstName": "Ada",
                                  "lastName": "Lovelace"
                                }
                              ]
                            }
                          }
                        ]
                      }
                    }
                    """));
            }

            if (request.RequestUri is not null &&
                request.RequestUri.AbsoluteUri.Contains("/EmpJob?", StringComparison.Ordinal) &&
                request.RequestUri.AbsoluteUri.Contains("U10000", StringComparison.Ordinal))
            {
                return Task.FromResult(JsonResponse(
                    """
                    {
                      "d": {
                        "results": [
                          {
                            "userId": "U10000",
                            "jobTitle": "Engineer",
                            "department": "Core Systems"
                          }
                        ]
                      }
                    }
                    """));
            }

            return Task.FromResult(JsonResponse("""{ "d": { "results": [] } }"""));
        }

        private static HttpResponseMessage JsonResponse(string json)
        {
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json")
            };
        }
    }
}

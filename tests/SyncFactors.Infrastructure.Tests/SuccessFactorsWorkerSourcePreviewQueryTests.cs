using Microsoft.Extensions.Logging.Abstractions;
using SyncFactors.Contracts;
using SyncFactors.Domain;
using SyncFactors.Infrastructure;
using System.Net;
using System.Net.Http;
using System.Text;

namespace SyncFactors.Infrastructure.Tests;

public sealed class SuccessFactorsWorkerSourcePreviewQueryTests
{
    [Theory]
    [InlineData("mock", true)]
    [InlineData("MOCK", true)]
    [InlineData("real", false)]
    [InlineData(null, false)]
    public void ScaffoldFallbackPolicy_IsEnabledOnlyForTheExplicitMockProfile(
        string? runProfile,
        bool expected)
    {
        Assert.Equal(
            expected,
            SuccessFactorsSourceSettings.FromRunProfile(runProfile).AllowScaffoldFallback);
    }

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

    [Fact]
    public async Task ListWorkersAsync_UsesPreviewLookupAndSkipsRowsWithoutEmploymentStatus()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "syncfactors-list-workers-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);

        try
        {
            var syncConfigPath = Path.Combine(tempRoot, "sync-config.json");
            var scaffoldDataPath = Path.Combine(tempRoot, "scaffold-data.json");
            await WriteSyncConfigAsync(syncConfigPath, includePreviewQuery: true);
            await WriteEmptyScaffoldDataAsync(scaffoldDataPath);

            var handler = new ServerPagedListMessageHandler();
            var source = CreateSource(syncConfigPath, scaffoldDataPath, handler, new StubDeltaSyncService());

            var workers = new List<WorkerSnapshot>();
            await foreach (var listedWorker in source.ListWorkersAsync(WorkerListingMode.Full, CancellationToken.None))
            {
                workers.Add(listedWorker);
            }

            var worker = Assert.Single(workers);
            Assert.Equal("U10001", worker.WorkerId);
            Assert.Equal("Grace", worker.PreferredName);
            Assert.Equal("Hopper", worker.LastName);
            Assert.Equal("Platform", worker.Department);
            Assert.Equal("Engineer", worker.Attributes["jobTitle"]);
            Assert.Equal("A", worker.Attributes["emplStatus"]);
            Assert.Contains(handler.RequestUris, uri => uri.Contains("/PerPerson?", StringComparison.Ordinal) && uri.Contains("customPageSize=2", StringComparison.Ordinal));
            Assert.Contains(handler.RequestUris, uri => uri.Contains("/EmpJob?", StringComparison.Ordinal) && uri.Contains("paging=snapshot", StringComparison.Ordinal));
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public async Task ListWorkersAsync_FallsBackToLegacyPagingWhenSnapshotPagingIsRejected()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "syncfactors-legacy-paging-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);

        try
        {
            var syncConfigPath = Path.Combine(tempRoot, "sync-config.json");
            var scaffoldDataPath = Path.Combine(tempRoot, "scaffold-data.json");
            await WriteSyncConfigAsync(syncConfigPath, includePreviewQuery: false);
            await WriteEmptyScaffoldDataAsync(scaffoldDataPath);

            var handler = new LegacyPagingFallbackMessageHandler();
            var source = CreateSource(syncConfigPath, scaffoldDataPath, handler, new DeltaWindowService(
                new DeltaSyncWindow(
                    Enabled: true,
                    HasCheckpoint: true,
                    Filter: "lastModifiedDateTime ge datetime'2026-04-01T00:00:00'",
                    DeltaField: "lastModifiedDateTime",
                    CheckpointUtc: DateTimeOffset.Parse("2026-04-01T00:00:00Z"),
                    EffectiveSinceUtc: DateTimeOffset.Parse("2026-04-01T00:00:00Z"))));

            var workers = new List<WorkerSnapshot>();
            await foreach (var listedWorker in source.ListWorkersAsync(WorkerListingMode.DeltaPreferred, CancellationToken.None))
            {
                workers.Add(listedWorker);
            }

            var worker = Assert.Single(workers);
            Assert.Equal("U20001", worker.WorkerId);
            Assert.Equal("Alan", worker.PreferredName);
            Assert.Equal("Math", worker.Department);
            Assert.Contains(handler.RequestUris, uri => uri.Contains("customPageSize=2", StringComparison.Ordinal));
            Assert.Contains(handler.RequestUris, uri => uri.Contains("$top=2", StringComparison.Ordinal) && uri.Contains("$skip=0", StringComparison.Ordinal));
            Assert.Contains(handler.RequestUris, uri =>
            {
                var decoded = Uri.UnescapeDataString(uri);
                return decoded.Contains("emplStatus eq 'A'", StringComparison.Ordinal) &&
                       decoded.Contains("lastModifiedDateTime ge datetime'2026-04-01T00:00:00'", StringComparison.Ordinal);
            });
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public async Task GetWorkerAsync_DoesNotReturnScaffoldWorkerWhenSuccessFactorsHasNoMatch()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "syncfactors-real-source-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);

        try
        {
            var syncConfigPath = Path.Combine(tempRoot, "sync-config.json");
            var scaffoldDataPath = Path.Combine(tempRoot, "scaffold-data.json");
            await WriteSyncConfigAsync(syncConfigPath, includePreviewQuery: false);
            await File.WriteAllTextAsync(
                scaffoldDataPath,
                """
                {
                  "workers": [
                    {
                      "workerId": "scaffold-only",
                      "preferredName": "Synthetic",
                      "lastName": "Worker",
                      "department": "Test",
                      "targetOu": "OU=LabUsers,DC=example,DC=com",
                      "isPrehire": false,
                      "attributes": {}
                    }
                  ],
                  "directoryUsers": []
                }
                """);

            var source = CreateSource(
                syncConfigPath,
                scaffoldDataPath,
                new EmptySuccessFactorsMessageHandler(),
                new StubDeltaSyncService());

            var worker = await source.GetWorkerAsync("scaffold-only", CancellationToken.None);

            Assert.Null(worker);
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    private static SuccessFactorsWorkerSource CreateSource(
        string syncConfigPath,
        string scaffoldDataPath,
        HttpMessageHandler handler,
        IDeltaSyncService deltaSyncService)
    {
        return new SuccessFactorsWorkerSource(
            new HttpClient(handler),
            new SyncFactorsConfigurationLoader(new SyncFactorsConfigPathResolver(syncConfigPath, null)),
            deltaSyncService,
            new ScaffoldWorkerSource(new ScaffoldDataStore(new ScaffoldDataPathResolver(scaffoldDataPath))),
            NullLogger<SuccessFactorsWorkerSource>.Instance);
    }

    private static async Task WriteSyncConfigAsync(string syncConfigPath, bool includePreviewQuery)
    {
        var previewQuery = includePreviewQuery
            ? """
              ,
                    "previewQuery": {
                      "entitySet": "PerPerson",
                      "identityField": "personIdExternal",
                      "deltaField": "lastModifiedDateTime",
                      "pageSize": 2,
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
                    }
              """
            : string.Empty;

        await File.WriteAllTextAsync(
            syncConfigPath,
            $$"""
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
                  "deltaSyncEnabled": true,
                  "baseFilter": "emplStatus eq 'A'",
                  "pageSize": 2,
                  "select": [ "userId", "firstName", "lastName", "jobTitle", "department", "emplStatus" ],
                  "expand": []
                }{{previewQuery}},
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
    }

    private static Task WriteEmptyScaffoldDataAsync(string scaffoldDataPath)
    {
        return File.WriteAllTextAsync(
            scaffoldDataPath,
            """
            {
              "workers": [],
              "directoryUsers": []
            }
            """);
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

    private sealed class DeltaWindowService(DeltaSyncWindow window) : IDeltaSyncService
    {
        public Task<DeltaSyncWindow> GetWindowAsync(CancellationToken cancellationToken)
        {
            _ = cancellationToken;
            return Task.FromResult(window);
        }

        public Task RecordSuccessfulRunAsync(DateTimeOffset checkpointUtc, CancellationToken cancellationToken)
        {
            _ = checkpointUtc;
            _ = cancellationToken;
            return Task.CompletedTask;
        }
    }

    private sealed class ServerPagedListMessageHandler : HttpMessageHandler
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
                            "personIdExternal": "10001",
                            "employmentNav": {
                              "results": [
                                {
                                  "userNav": { "userId": "U10001" },
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
                                  "firstName": "Grace",
                                  "lastName": "Hopper"
                                }
                              ]
                            }
                          },
                          {
                            "personIdExternal": "10002",
                            "employmentNav": {
                              "results": [
                                {
                                  "userNav": { "userId": "U10002" },
                                  "jobInfoNav": {
                                    "results": [
                                      {
                                        "departmentNav": {
                                          "name_localized": "Operations"
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
                                  "firstName": "Katherine",
                                  "lastName": "Johnson"
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
                request.RequestUri.AbsoluteUri.Contains("/EmpJob?", StringComparison.Ordinal))
            {
                return Task.FromResult(JsonResponse(
                    """
                    {
                      "d": {
                        "results": [
                          {
                            "userId": "U10001",
                            "firstName": "Primary",
                            "lastName": "Worker",
                            "jobTitle": "Engineer",
                            "department": "Core Systems",
                            "emplStatus": "A"
                          },
                          {
                            "userId": "U10002",
                            "firstName": "Missing",
                            "lastName": "Status",
                            "jobTitle": "Analyst",
                            "department": "Ops"
                          }
                        ]
                      }
                    }
                    """));
            }

            return Task.FromResult(JsonResponse("""{ "d": { "results": [] } }"""));
        }
    }

    private sealed class LegacyPagingFallbackMessageHandler : HttpMessageHandler
    {
        public List<string> RequestUris { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            _ = cancellationToken;
            var uri = request.RequestUri?.ToString() ?? string.Empty;
            RequestUris.Add(uri);

            if (uri.Contains("customPageSize=2", StringComparison.Ordinal))
            {
                return Task.FromResult(JsonResponse(
                    """
                    {
                      "error": {
                        "message": {
                          "value": "Snapshot paging is not supported."
                        }
                      }
                    }
                    """,
                    HttpStatusCode.BadRequest));
            }

            if (uri.Contains("$skip=0", StringComparison.Ordinal))
            {
                return Task.FromResult(JsonResponse(
                    """
                    {
                      "d": {
                        "results": [
                          {
                            "userId": "U20001",
                            "firstName": "Alan",
                            "lastName": "Turing",
                            "jobTitle": "Researcher",
                            "department": "Math",
                            "emplStatus": "A"
                          }
                        ]
                      }
                    }
                    """));
            }

            return Task.FromResult(JsonResponse("""{ "d": { "results": [] } }"""));
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

    }

    private sealed class EmptySuccessFactorsMessageHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            _ = request;
            _ = cancellationToken;
            return Task.FromResult(JsonResponse("""{ "d": { "results": [] } }"""));
        }
    }

    private static HttpResponseMessage JsonResponse(string json, HttpStatusCode statusCode = HttpStatusCode.OK)
    {
        return new HttpResponseMessage(statusCode)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };
    }
}

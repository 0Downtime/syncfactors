using Microsoft.Extensions.Logging.Abstractions;
using SyncFactors.Infrastructure;
using System.Net;
using System.Net.Http;
using System.Text;

namespace SyncFactors.Infrastructure.Tests;

public sealed class SuccessFactorsUserLookupServiceTests
{
    [Fact]
    public async Task LookupAsync_QueriesUserAndPersonRecordsWithoutWorkflowMappings()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "syncfactors-user-lookup-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);

        try
        {
            var syncConfigPath = Path.Combine(tempRoot, "sync-config.json");
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
                      "identityField": "customWorkflowIdentity",
                      "deltaField": "lastModifiedDateTime",
                      "select": [ "customWorkflowIdentity" ],
                      "expand": []
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

            var handler = new LookupMessageHandler();
            var service = new SuccessFactorsUserLookupService(
                new HttpClient(handler),
                new SyncFactorsConfigurationLoader(new SyncFactorsConfigPathResolver(syncConfigPath, null)),
                NullLogger<SuccessFactorsUserLookupService>.Instance);

            var result = await service.LookupAsync("U10000", CancellationToken.None);

            Assert.True(result.HasMatches);
            Assert.Contains(handler.RequestUris, uri => uri.Contains("/EmpJob?", StringComparison.Ordinal) && uri.Contains("userId", StringComparison.Ordinal) && uri.Contains("U10000", StringComparison.Ordinal));
            Assert.Contains(handler.RequestUris, uri => uri.Contains("/PerPerson?", StringComparison.Ordinal) && uri.Contains("personIdExternal", StringComparison.Ordinal) && uri.Contains("P10000", StringComparison.Ordinal));
            Assert.Contains(result.Attributes, attribute =>
                attribute.EntitySet == "PerPerson" &&
                attribute.Path == "[0].personalInfoNav[0].firstName" &&
                attribute.Value == "Ada");
            Assert.Contains(result.Attributes, attribute =>
                attribute.EntitySet == "EmpJob" &&
                attribute.Path == "[0].jobTitle" &&
                attribute.Value == "Engineer");
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    private sealed class LookupMessageHandler : HttpMessageHandler
    {
        public List<string> RequestUris { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            _ = cancellationToken;
            RequestUris.Add(request.RequestUri?.ToString() ?? string.Empty);
            Assert.NotNull(request.Headers.Authorization);
            Assert.Equal("Basic", request.Headers.Authorization!.Scheme);

            var uri = request.RequestUri?.AbsoluteUri ?? string.Empty;
            if (uri.Contains("/EmpJob?", StringComparison.Ordinal) &&
                uri.Contains("U10000", StringComparison.Ordinal))
            {
                return Task.FromResult(JsonResponse(
                    """
                    {
                      "d": {
                        "results": [
                          {
                            "userId": "U10000",
                            "personIdExternal": "P10000",
                            "jobTitle": "Engineer"
                          }
                        ]
                      }
                    }
                    """));
            }

            if (uri.Contains("/PerPerson?", StringComparison.Ordinal) &&
                uri.Contains("P10000", StringComparison.Ordinal))
            {
                return Task.FromResult(JsonResponse(
                    """
                    {
                      "d": {
                        "results": [
                          {
                            "personIdExternal": "P10000",
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

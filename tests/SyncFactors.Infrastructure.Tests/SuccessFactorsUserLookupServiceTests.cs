using Microsoft.Extensions.Logging.Abstractions;
using SyncFactors.Infrastructure;
using System.Net;
using System.Net.Http;
using System.Text;

namespace SyncFactors.Infrastructure.Tests;

public sealed class SuccessFactorsUserLookupServiceTests
{
    [Fact]
    public async Task LookupAsync_Throws_WhenLookupValueIsBlank()
    {
        var service = new SuccessFactorsUserLookupService(
            new HttpClient(new LookupMessageHandler()),
            new SyncFactorsConfigurationLoader(new SyncFactorsConfigPathResolver("/tmp/missing-sync-config.json", null)),
            NullLogger<SuccessFactorsUserLookupService>.Instance);

        await Assert.ThrowsAsync<ArgumentException>(() => service.LookupAsync(" ", CancellationToken.None));
    }

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

    [Fact]
    public async Task LookupAsync_RetriesWithoutExpand_WhenExpandedLookupIsRejected()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "syncfactors-user-lookup-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);

        try
        {
            var service = await CreateServiceAsync(tempRoot, new ExpandFallbackMessageHandler());

            var result = await service.LookupAsync("P10000", CancellationToken.None);

            var perPerson = Assert.Single(result.EntityResults, entity => entity.EntitySet == "PerPerson");
            Assert.True(perPerson.IsSuccess);
            Assert.Equal(1, perPerson.ItemCount);
            Assert.Contains("retried without $expand", perPerson.Error, StringComparison.OrdinalIgnoreCase);
            Assert.Contains(result.Attributes, attribute => attribute.Path == "[0].active" && attribute.Value == "true");
            Assert.DoesNotContain(result.Attributes, attribute => attribute.Path.Contains("__metadata", StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public async Task LookupAsync_RecordsInvalidJsonAndHttpFailures()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "syncfactors-user-lookup-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);

        try
        {
            var service = await CreateServiceAsync(tempRoot, new FailureMessageHandler());

            var result = await service.LookupAsync("U10000", CancellationToken.None);

            Assert.False(result.HasMatches);
            Assert.Contains(result.EntityResults, entity =>
                entity.EntitySet == "PerPerson" &&
                !entity.IsSuccess &&
                entity.Error is not null &&
                entity.Error.Contains("invalid JSON", StringComparison.OrdinalIgnoreCase));
            Assert.Contains(result.EntityResults, entity =>
                entity.EntitySet == "EmpJob" &&
                !entity.IsSuccess &&
                entity.Error is not null &&
                entity.Error.Contains("Status=503", StringComparison.OrdinalIgnoreCase) &&
                entity.Error.Contains("maintenance", StringComparison.OrdinalIgnoreCase));
            Assert.Contains(result.EntityResults, entity =>
                entity.EntitySet == "User" &&
                !entity.IsSuccess &&
                entity.Error is not null &&
                entity.Error.Contains("ContentType=text/plain", StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public async Task LookupAsync_UsesOAuthBearerToken()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "syncfactors-user-lookup-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);

        try
        {
            var handler = new OAuthMessageHandler();
            var service = await CreateServiceAsync(
                tempRoot,
                handler,
                """
                {
                  "mode": "oauth",
                  "oauth": {
                    "tokenUrl": "http://sf.example/oauth/token",
                    "clientId": "client-1",
                    "clientSecret": "secret-1",
                    "companyId": "company-1"
                  }
                }
                """);

            var result = await service.LookupAsync("U10000", CancellationToken.None);

            Assert.False(result.HasMatches);
            Assert.True(handler.TokenRequested);
            Assert.Contains("company_id=company-1", handler.TokenForm, StringComparison.Ordinal);
            Assert.All(handler.ODataAuthorizations, authorization => Assert.Equal("Bearer token-1", authorization));
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public async Task LookupAsync_Throws_WhenOAuthTokenResponseHasNoAccessToken()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "syncfactors-user-lookup-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);

        try
        {
            var service = await CreateServiceAsync(
                tempRoot,
                new MissingOAuthTokenMessageHandler(),
                """
                {
                  "mode": "oauth",
                  "oauth": {
                    "tokenUrl": "http://sf.example/oauth/token",
                    "clientId": "client-1",
                    "clientSecret": "secret-1"
                  }
                }
                """);

            var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                service.LookupAsync("U10000", CancellationToken.None));
            Assert.Contains("access_token", exception.Message, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    private static async Task<SuccessFactorsUserLookupService> CreateServiceAsync(
        string tempRoot,
        HttpMessageHandler handler,
        string? authJson = null)
    {
        var syncConfigPath = Path.Combine(tempRoot, "sync-config.json");
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
                  "select": [ "userId" ],
                  "expand": []
                },
                "auth": {{authJson ?? """
                {
                  "mode": "basic",
                  "basic": {
                    "username": "user",
                    "password": "pass"
                  }
                }
                """}}
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

        return new SuccessFactorsUserLookupService(
            new HttpClient(handler),
            new SyncFactorsConfigurationLoader(new SyncFactorsConfigPathResolver(syncConfigPath, null)),
            NullLogger<SuccessFactorsUserLookupService>.Instance);
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

    private sealed class ExpandFallbackMessageHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            _ = cancellationToken;
            Assert.NotNull(request.Headers.Authorization);

            var uri = request.RequestUri?.AbsoluteUri ?? string.Empty;
            if (uri.Contains("/PerPerson?", StringComparison.Ordinal) &&
                uri.Contains("$expand=", StringComparison.Ordinal))
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.BadRequest)
                {
                    Content = new StringContent("""{"error":{"message":"expand rejected"}}""", Encoding.UTF8, "application/json")
                });
            }

            if (uri.Contains("/PerPerson?", StringComparison.Ordinal))
            {
                return Task.FromResult(JsonResponse(
                    """
                    {
                      "value": [
                        {
                          "__metadata": { "uri": "ignored" },
                          "personIdExternal": "P10000",
                          "userId": "U10000",
                          "active": true,
                          "rank": 3,
                          "nickname": null
                        }
                      ]
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

    private sealed class FailureMessageHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            _ = cancellationToken;
            var uri = request.RequestUri?.AbsoluteUri ?? string.Empty;
            if (uri.Contains("/PerPerson?", StringComparison.Ordinal))
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("{not-json", Encoding.UTF8, "application/json")
                });
            }

            if (uri.Contains("/EmpJob?", StringComparison.Ordinal))
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
                {
                    Content = new StringContent("maintenance", Encoding.UTF8, "text/plain")
                });
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.InternalServerError)
            {
                Content = new StringContent(string.Empty)
            });
        }
    }

    private sealed class OAuthMessageHandler : HttpMessageHandler
    {
        public bool TokenRequested { get; private set; }
        public string TokenForm { get; private set; } = string.Empty;
        public List<string> ODataAuthorizations { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            _ = cancellationToken;
            var uri = request.RequestUri?.AbsoluteUri ?? string.Empty;
            if (uri.Contains("/oauth/token", StringComparison.Ordinal))
            {
                TokenRequested = true;
                TokenForm = await request.Content!.ReadAsStringAsync(cancellationToken);
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("""{"access_token":"token-1"}""", Encoding.UTF8, "application/json")
                };
            }

            ODataAuthorizations.Add(request.Headers.Authorization?.ToString() ?? string.Empty);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""{ "d": { "results": [] } }""", Encoding.UTF8, "application/json")
            };
        }
    }

    private sealed class MissingOAuthTokenMessageHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            _ = request;
            _ = cancellationToken;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""{"token_type":"Bearer"}""", Encoding.UTF8, "application/json")
            });
        }
    }
}

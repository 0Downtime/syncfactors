using Microsoft.Extensions.Logging.Abstractions;
using SyncFactors.Api.Pages;
using SyncFactors.Infrastructure;
using System.Net;
using System.Text;

namespace SyncFactors.Api.Tests;

public sealed class LookupModelTests
{
    [Fact]
    public async Task OnGetAsync_DoesNothing_WhenLookupValueIsBlank()
    {
        var model = new LookupModel(await CreateLookupServiceAsync(new LookupHandler()));

        await model.OnGetAsync(CancellationToken.None);

        Assert.Null(model.Result);
        Assert.Null(model.ErrorMessage);
    }

    [Fact]
    public async Task OnGetAsync_LoadsResult_WhenLookupSucceeds()
    {
        var model = new LookupModel(await CreateLookupServiceAsync(new LookupHandler()))
        {
            LookupValue = " U10000 "
        };

        await model.OnGetAsync(CancellationToken.None);

        Assert.NotNull(model.Result);
        Assert.True(model.Result!.HasMatches);
        Assert.Equal("U10000", model.Result.LookupValue);
        Assert.Null(model.ErrorMessage);
    }

    [Fact]
    public async Task OnGetAsync_CapturesSupportedLookupErrors()
    {
        var model = new LookupModel(await CreateLookupServiceAsync(new ThrowingLookupHandler()))
        {
            LookupValue = "U10000"
        };

        await model.OnGetAsync(CancellationToken.None);

        Assert.Null(model.Result);
        Assert.Equal("SuccessFactors request failed.", model.ErrorMessage);
    }

    private static async Task<SuccessFactorsUserLookupService> CreateLookupServiceAsync(HttpMessageHandler handler)
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "syncfactors-lookup-model-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);
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
                  "identityField": "userId",
                  "deltaField": "lastModifiedDateTime",
                  "select": [ "userId" ],
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

        return new SuccessFactorsUserLookupService(
            new HttpClient(handler),
            new SyncFactorsConfigurationLoader(new SyncFactorsConfigPathResolver(syncConfigPath, null)),
            NullLogger<SuccessFactorsUserLookupService>.Instance);
    }

    private sealed class LookupHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            _ = cancellationToken;
            var uri = request.RequestUri?.AbsoluteUri ?? string.Empty;
            if (uri.Contains("/EmpJob?", StringComparison.Ordinal))
            {
                return Task.FromResult(JsonResponse(
                    """
                    {
                      "d": {
                        "results": [
                          {
                            "userId": "U10000",
                            "personIdExternal": "P10000"
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

    private sealed class ThrowingLookupHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            _ = request;
            _ = cancellationToken;
            throw new HttpRequestException("SuccessFactors request failed.");
        }
    }
}

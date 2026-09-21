using System.Net;
using System.Text;
using SyncFactors.Automation;

namespace SyncFactors.Automation.Tests;

public sealed class AutomationAntiforgeryTests
{
    [Fact]
    public async Task AuthenticateAsync_RefreshesAntiforgeryTokenBeforeAndAfterLogin()
    {
        var handler = new RecordingHandler();
        var options = new AutomationOptions(
            ScenarioPatterns: [],
            ReportPath: "report.md",
            ApiUrl: new Uri("https://syncfactors.example.test"),
            MockUrl: new Uri("https://mock.example.test"),
            Username: "automation",
            Password: "password",
            AllowAdReset: false,
            Tags: new HashSet<string>(),
            ConfigPath: null,
            MappingConfigPath: null,
            Timeout: TimeSpan.FromSeconds(5),
            IncludeDestructive: false,
            IncludeScale: false,
            IncludeRecovery: false,
            Idempotency: false);
        await using var runner = new AutomationRunner(options, TextWriter.Null, handler);

        await runner.AuthenticateAsync(CancellationToken.None);

        Assert.Equal(
            ["GET /api/session/antiforgery", "POST /api/session/login", "GET /api/session/antiforgery"],
            handler.Requests);
        Assert.Equal("anonymous-token", handler.LoginAntiforgeryToken);
    }

    private sealed class RecordingHandler : HttpMessageHandler
    {
        private int _antiforgeryRequestCount;

        public List<string> Requests { get; } = [];

        public string? LoginAntiforgeryToken { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Requests.Add($"{request.Method} {request.RequestUri?.AbsolutePath}");
            if (request.Method == HttpMethod.Get &&
                request.RequestUri?.AbsolutePath == "/api/session/antiforgery")
            {
                _antiforgeryRequestCount++;
                var token = _antiforgeryRequestCount == 1 ? "anonymous-token" : "authenticated-token";
                return Task.FromResult(JsonResponse($"{{\"requestToken\":\"{token}\"}}"));
            }

            if (request.Method == HttpMethod.Post && request.RequestUri?.AbsolutePath == "/api/session/login")
            {
                LoginAntiforgeryToken = request.Headers.GetValues("X-SyncFactors-Antiforgery").Single();
                return Task.FromResult(JsonResponse("{}"));
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        }

        private static HttpResponseMessage JsonResponse(string json) => new(HttpStatusCode.OK)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };
    }
}

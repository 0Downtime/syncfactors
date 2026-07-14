using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using SyncFactors.Infrastructure;

namespace SyncFactors.Api.Tests;

public sealed class MutationAuthorizationIntegrationTests
{
    private const string Password = "IntegrationPass123!";
    private static readonly SemaphoreSlim HostInitializationLock = new(1, 1);

    [Fact]
    public async Task AnonymousRequests_ToEveryProtectedMutationEndpoint_ReturnUnauthorized()
    {
        await using var fixture = await AuthorizationFixture.CreateAsync();
        using var client = fixture.CreateClient(handleCookies: false);

        foreach (var endpoint in MutationEndpoints)
        {
            using var response = await client.SendAsync(endpoint.CreateRequest());

            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        }
    }

    [Theory]
    [InlineData(SecurityRoles.Viewer)]
    [InlineData(SecurityRoles.Operator)]
    [InlineData(SecurityRoles.Admin)]
    [InlineData(SecurityRoles.BreakGlassAdmin)]
    public async Task CookieAuthenticatedRoles_EnforceEveryMutationEndpointPolicy(string role)
    {
        await using var fixture = await AuthorizationFixture.CreateAsync();
        using var client = await fixture.SignInAsync(role);

        foreach (var endpoint in MutationEndpoints)
        {
            using var response = await client.SendAsync(endpoint.CreateRequest());

            if (endpoint.RequiredRole == RequiredRole.Operator && HasOperatorAccess(role) ||
                endpoint.RequiredRole == RequiredRole.Admin && HasAdminAccess(role))
            {
                Assert.NotEqual(HttpStatusCode.Unauthorized, response.StatusCode);
                Assert.NotEqual(HttpStatusCode.Forbidden, response.StatusCode);
            }
            else
            {
                Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
            }
        }
    }

    [Fact]
    public async Task SessionLogin_IssuesHttpOnlyCookie_ThatAuthorizesMutationsWithoutAntiforgeryHeader()
    {
        await using var fixture = await AuthorizationFixture.CreateAsync();
        using var client = await fixture.SignInAsync(SecurityRoles.Operator);

        using var response = await client.PostAsJsonAsync("/api/runs/cancel", new { });

        Assert.NotEqual(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.NotEqual(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.DoesNotContain(client.DefaultRequestHeaders, header =>
            string.Equals(header.Key, "RequestVerificationToken", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task CookieAuthenticatedRazorMutation_RejectsRequestsWithoutAntiforgeryToken()
    {
        await using var fixture = await AuthorizationFixture.CreateAsync();
        using var client = await fixture.SignInAsync(SecurityRoles.Operator);

        using var response = await client.PostAsync(
            "/Sync?handler=StartRun",
            new FormUrlEncodedContent([new KeyValuePair<string, string>("RunMode", "DryRun")]));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    private static bool HasOperatorAccess(string role) =>
        string.Equals(role, SecurityRoles.Operator, StringComparison.Ordinal) || HasAdminAccess(role);

    private static bool HasAdminAccess(string role) =>
        string.Equals(role, SecurityRoles.Admin, StringComparison.Ordinal) ||
        string.Equals(role, SecurityRoles.BreakGlassAdmin, StringComparison.Ordinal);

    private static readonly MutationEndpoint[] MutationEndpoints =
    [
        new(HttpMethod.Post, "/api/runs", RequiredRole.Operator, new { dryRun = true }),
        new(HttpMethod.Post, "/api/runs/cancel", RequiredRole.Operator, new { }),
        new(HttpMethod.Put, "/api/sync/schedule", RequiredRole.Admin, new { enabled = false, intervalMinutes = 30 }),
        new(HttpMethod.Post, "/api/previews", RequiredRole.Operator, new { workerId = "missing-worker" }),
        new(HttpMethod.Post, "/api/preview/missing-worker/apply", RequiredRole.Operator, new { workerId = "different-worker", previewRunId = "missing-run" }),
        new(HttpMethod.Post, "/api/runs/full", RequiredRole.Operator, new { dryRun = true, acknowledgeRealSync = false }),
        new(HttpMethod.Post, "/api/runs/delete-all", RequiredRole.Admin, new { confirmationText = "not-confirmed" }),
        new(HttpMethod.Post, "/api/admin/runs/queue/recovery-probe", RequiredRole.Operator, new { status = "invalid" }),
        new(HttpMethod.Post, "/api/admin/users", RequiredRole.Admin, new { username = "", password = "", isAdmin = false }),
        new(HttpMethod.Post, "/api/admin/users/missing-user/password", RequiredRole.Admin, new { newPassword = "IntegrationPass123!" }),
        new(HttpMethod.Post, "/api/admin/users/missing-user/role", RequiredRole.Admin, new { isAdmin = false, role = "Viewer" }),
        new(HttpMethod.Post, "/api/admin/users/missing-user/active", RequiredRole.Admin, new { isActive = false }),
        new(HttpMethod.Delete, "/api/admin/users/missing-user", RequiredRole.Admin, null)
    ];

    private enum RequiredRole
    {
        Operator,
        Admin
    }

    private sealed record MutationEndpoint(HttpMethod Method, string Path, RequiredRole RequiredRole, object? Payload)
    {
        public HttpRequestMessage CreateRequest() => new(Method, Path)
        {
            Content = Payload is null ? null : JsonContent.Create(Payload)
        };
    }

    private sealed class AuthorizationFixture : IAsyncDisposable
    {
        private readonly string _runtimeDirectory;
        private readonly ApiFactory _factory;

        private AuthorizationFixture(string runtimeDirectory, ApiFactory factory)
        {
            _runtimeDirectory = runtimeDirectory;
            _factory = factory;
        }

        public static async Task<AuthorizationFixture> CreateAsync()
        {
            await HostInitializationLock.WaitAsync();
            var settings = new Dictionary<string, string?>
            {
                ["ASPNETCORE_ENVIRONMENT"] = "Development",
                ["SYNCFACTORS_RUN_PROFILE"] = "mock",
                ["SYNCFACTORS_CONFIG_PATH"] = ApiFactory.RepositoryPath("config", "sample.mock-successfactors.real-ad.sync-config.json"),
                ["SYNCFACTORS_MAPPING_CONFIG_PATH"] = ApiFactory.RepositoryPath("config", "sample.empjob-confirmed.mapping-config.json"),
                ["SF_AD_SYNC_SF_CLIENT_ID"] = "integration-test-client",
                ["SF_AD_SYNC_SF_CLIENT_SECRET"] = "integration-test-secret",
                ["SF_AD_SYNC_AD_SERVER"] = "ldap.integration.test",
                ["SyncFactors__SqlitePath"] = string.Empty,
                ["SyncFactors__ScaffoldDataPath"] = ApiFactory.RepositoryPath("src", "SyncFactors.Api", "config", "scaffold-data.json"),
                ["SyncFactors__Runtime__DryRunOnly"] = "true",
                ["SyncFactors__Realtime__Enabled"] = "false",
                ["SyncFactors__Auth__Mode"] = "local-break-glass",
                ["SyncFactors__Auth__BootstrapAdmin__Username"] = "breakglass",
                ["SyncFactors__Auth__BootstrapAdmin__Password"] = Password,
                ["SyncFactors__Auth__LocalBreakGlass__Enabled"] = "true"
            };
            var previousSettings = settings.ToDictionary(pair => pair.Key, pair => Environment.GetEnvironmentVariable(pair.Key));
            var runtimeDirectory = Path.Combine(Path.GetTempPath(), $"syncfactors-authz-{Guid.NewGuid():N}");
            settings["SyncFactors__SqlitePath"] = Path.Combine(runtimeDirectory, "syncfactors.db");
            try
            {
                foreach (var (name, value) in settings)
                {
                    Environment.SetEnvironmentVariable(name, value);
                }

                Directory.CreateDirectory(runtimeDirectory);
                var fixture = new AuthorizationFixture(runtimeDirectory, new ApiFactory());
                await fixture.CreateLocalUsersAsync();
                return fixture;
            }
            finally
            {
                foreach (var (name, value) in previousSettings)
                {
                    Environment.SetEnvironmentVariable(name, value);
                }

                HostInitializationLock.Release();
            }
        }

        public HttpClient CreateClient(bool handleCookies) => _factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            BaseAddress = new Uri("http://localhost"),
            HandleCookies = handleCookies
        });

        public async Task<HttpClient> SignInAsync(string role)
        {
            var client = CreateClient(handleCookies: true);
            var username = string.Equals(role, SecurityRoles.BreakGlassAdmin, StringComparison.Ordinal)
                ? "breakglass"
                : role.ToLowerInvariant();

            using var response = await client.PostAsJsonAsync("/api/session/login", new
            {
                username,
                password = Password,
                rememberMe = false,
                returnUrl = (string?)null
            });

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Contains(response.Headers.GetValues("Set-Cookie"), value =>
                value.StartsWith("SyncFactors.Auth=", StringComparison.Ordinal) &&
                value.Contains("httponly", StringComparison.OrdinalIgnoreCase));

            return client;
        }

        public async ValueTask DisposeAsync()
        {
            await _factory.DisposeAsync();
            Directory.Delete(_runtimeDirectory, recursive: true);
        }

        private async Task CreateLocalUsersAsync()
        {
            var authService = _factory.Services.GetRequiredService<ILocalAuthService>();
            foreach (var role in new[] { SecurityRoles.Viewer, SecurityRoles.Operator, SecurityRoles.Admin })
            {
                var result = await authService.CreateUserAsync(role.ToLowerInvariant(), Password, role, CancellationToken.None);
                Assert.True(result.Succeeded, result.Message);
            }
        }
    }

    private sealed class ApiFactory : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Development");
        }

        internal static string RepositoryPath(params string[] segments)
        {
            var directory = new DirectoryInfo(AppContext.BaseDirectory);
            while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "SyncFactors.Next.sln")))
            {
                directory = directory.Parent;
            }

            if (directory is null)
            {
                throw new InvalidOperationException("Repository root could not be located for integration-test configuration.");
            }

            return Path.Combine([directory.FullName, .. segments]);
        }
    }
}

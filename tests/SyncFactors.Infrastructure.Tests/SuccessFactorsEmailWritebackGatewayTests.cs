using Microsoft.Extensions.Logging.Abstractions;
using SyncFactors.Contracts;
using SyncFactors.Domain;
using SyncFactors.Infrastructure;
using System.Net;
using System.Net.Http;
using System.Text.Json;

namespace SyncFactors.Infrastructure.Tests;

public sealed class SuccessFactorsEmailWritebackGatewayTests
{
    [Fact]
    public async Task WriteBackEmailAsync_PostsUserUpsertPayload_WhenEnabledAndEmailChanged()
    {
        var tempRoot = CreateTempRoot();
        try
        {
            var syncConfigPath = Path.Combine(tempRoot, "sync-config.json");
            await WriteSyncConfigAsync(syncConfigPath, enabled: true);
            var handler = new CapturingEmailWritebackHandler("""
                {"d":[{"key":"User/userId=10001","status":"OK","editStatus":"UPSERTED","message":null,"index":0,"httpCode":200,"inlineResults":null}]}
                """);
            var gateway = CreateGateway(syncConfigPath, handler);

            var result = await gateway.WriteBackEmailAsync(
                CreatePlan(sourceEmail: "old@example.test"),
                CreateCommand(mail: "new@example.test"),
                dryRun: false,
                CancellationToken.None);

            Assert.NotNull(result);
            Assert.True(result.Succeeded);
            Assert.True(result.Applied);
            Assert.Equal("http://sf.example/odata/v2/User/upsert", handler.RequestUri);
            Assert.Equal("Basic", handler.AuthorizationScheme);
            using var payload = JsonDocument.Parse(handler.RequestBody!);
            Assert.Equal("User('10001')", payload.RootElement.GetProperty("__metadata").GetProperty("uri").GetString());
            Assert.Equal("SFOData.User", payload.RootElement.GetProperty("__metadata").GetProperty("type").GetString());
            Assert.Equal("10001", payload.RootElement.GetProperty("userId").GetString());
            Assert.Equal("new@example.test", payload.RootElement.GetProperty("email").GetString());
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public async Task WriteBackEmailAsync_ReturnsPlannedResult_ForDryRun()
    {
        var tempRoot = CreateTempRoot();
        try
        {
            var syncConfigPath = Path.Combine(tempRoot, "sync-config.json");
            await WriteSyncConfigAsync(syncConfigPath, enabled: true);
            var handler = new CapturingEmailWritebackHandler("{}");
            var gateway = CreateGateway(syncConfigPath, handler);

            var result = await gateway.WriteBackEmailAsync(
                CreatePlan(sourceEmail: "old@example.test"),
                CreateCommand(mail: "new@example.test"),
                dryRun: true,
                CancellationToken.None);

            Assert.NotNull(result);
            Assert.True(result.Succeeded);
            Assert.False(result.Applied);
            Assert.Equal("SuccessFactors email writeback planned.", result.Message);
            Assert.Null(handler.RequestUri);
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public async Task WriteBackEmailAsync_ReturnsNull_WhenDisabled()
    {
        var tempRoot = CreateTempRoot();
        try
        {
            var syncConfigPath = Path.Combine(tempRoot, "sync-config.json");
            await WriteSyncConfigAsync(syncConfigPath, enabled: false);
            var handler = new CapturingEmailWritebackHandler("{}");
            var gateway = CreateGateway(syncConfigPath, handler);

            var result = await gateway.WriteBackEmailAsync(
                CreatePlan(sourceEmail: "old@example.test"),
                CreateCommand(mail: "new@example.test"),
                dryRun: false,
                CancellationToken.None);

            Assert.Null(result);
            Assert.Null(handler.RequestUri);
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public async Task WriteBackEmailAsync_SurfacesUpsertFailure_WhenODataResponseContainsError()
    {
        var tempRoot = CreateTempRoot();
        try
        {
            var syncConfigPath = Path.Combine(tempRoot, "sync-config.json");
            await WriteSyncConfigAsync(syncConfigPath, enabled: true);
            var handler = new CapturingEmailWritebackHandler("""
                {"d":[{"key":"User/userId=10001","status":"ERROR","editStatus":"FAILED","message":"email rejected","index":0,"httpCode":500,"inlineResults":null}]}
                """);
            var gateway = CreateGateway(syncConfigPath, handler);

            var result = await gateway.WriteBackEmailAsync(
                CreatePlan(sourceEmail: "old@example.test"),
                CreateCommand(mail: "new@example.test"),
                dryRun: false,
                CancellationToken.None);

            Assert.NotNull(result);
            Assert.False(result.Succeeded);
            Assert.True(result.Applied);
            Assert.Contains("email rejected", result.Message, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    private static string CreateTempRoot()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "syncfactors-email-writeback-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);
        return tempRoot;
    }

    private static SuccessFactorsEmailWritebackGateway CreateGateway(string syncConfigPath, HttpMessageHandler handler)
    {
        return new SuccessFactorsEmailWritebackGateway(
            new HttpClient(handler),
            new SyncFactorsConfigurationLoader(new SyncFactorsConfigPathResolver(syncConfigPath, null)),
            NullLogger<SuccessFactorsEmailWritebackGateway>.Instance);
    }

    private static PlannedWorkerAction CreatePlan(string sourceEmail)
    {
        var worker = new WorkerSnapshot(
            WorkerId: "10001",
            PreferredName: "Amy",
            LastName: "Craig",
            Department: "IT",
            TargetOu: "OU=Active,DC=example,DC=test",
            IsPrehire: false,
            Attributes: new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
            {
                ["userId"] = "10001",
                ["email"] = sourceEmail
            });

        var directoryUser = new DirectoryUserSnapshot(
            SamAccountName: "acraig",
            DistinguishedName: "CN=Amy Craig,OU=Active,DC=example,DC=test",
            Enabled: true,
            DisplayName: "Amy Craig",
            Attributes: new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase));

        return new PlannedWorkerAction(
            Worker: worker,
            DirectoryUser: directoryUser,
            Identity: new IdentityMatchResult("updates", true, "acraig", null, null),
            ManagerDistinguishedName: null,
            ProposedEmailAddress: "new@example.test",
            AttributeChanges:
            [
                new AttributeChange("mail", "email", sourceEmail, "new@example.test", true)
            ],
            MissingSourceAttributes: [],
            Bucket: "updates",
            CurrentOu: "OU=Active,DC=example,DC=test",
            TargetOu: "OU=Active,DC=example,DC=test",
            CurrentEnabled: true,
            TargetEnabled: true,
            PrimaryAction: "UpdateAttributes",
            Operations: [new DirectoryOperation("UpdateAttributes")],
            ReviewCategory: null,
            ReviewCaseType: null,
            Reason: null,
            CanAutoApply: true,
            DecisionSteps: null);
    }

    private static DirectoryMutationCommand CreateCommand(string mail)
    {
        return new DirectoryMutationCommand(
            Action: "UpdateAttributes",
            WorkerId: "10001",
            ManagerId: null,
            ManagerDistinguishedName: null,
            SamAccountName: "acraig",
            CommonName: "Amy Craig",
            UserPrincipalName: mail,
            Mail: mail,
            TargetOu: "OU=Active,DC=example,DC=test",
            DisplayName: "Amy Craig",
            CurrentDistinguishedName: "CN=Amy Craig,OU=Active,DC=example,DC=test",
            EnableAccount: true,
            Operations: [new DirectoryOperation("UpdateAttributes")],
            Attributes: new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase));
    }

    private static async Task WriteSyncConfigAsync(string path, bool enabled)
    {
        var enabledLiteral = enabled ? "true" : "false";
        await File.WriteAllTextAsync(
            path,
            $$"""
            {
              "secrets": {},
              "ad": {
                "server": "ldap.example.invalid",
                "username": "",
                "bindPassword": "",
                "defaultActiveOu": "OU=Active,DC=example,DC=test",
                "prehireOu": "OU=Prehire,DC=example,DC=test",
                "graveyardOu": "OU=Graveyard,DC=example,DC=test",
                "identityAttribute": "employeeID"
              },
              "successFactors": {
                "baseUrl": "http://sf.example/odata/v2",
                "auth": {
                  "mode": "basic",
                  "basic": {
                    "username": "api-user",
                    "password": "api-password"
                  }
                },
                "emailWriteback": {
                  "enabled": {{enabledLiteral}},
                  "userEntitySet": "User",
                  "userIdSourceAttribute": "userId",
                  "emailField": "email",
                  "sourceEmailAttribute": "email"
                },
                "query": {
                  "entitySet": "EmpJob",
                  "identityField": "userId",
                  "deltaField": "lastModifiedDateTime",
                  "pageSize": 10,
                  "select": ["userId"],
                  "expand": []
                }
              },
              "sync": {
                "enableBeforeStartDays": 7,
                "deletionRetentionDays": 45
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
    }

    private sealed class CapturingEmailWritebackHandler(string responseBody) : HttpMessageHandler
    {
        public string? RequestUri { get; private set; }
        public string? RequestBody { get; private set; }
        public string? AuthorizationScheme { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestUri = request.RequestUri?.ToString();
            AuthorizationScheme = request.Headers.Authorization?.Scheme;
            RequestBody = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(responseBody)
            };
        }
    }
}

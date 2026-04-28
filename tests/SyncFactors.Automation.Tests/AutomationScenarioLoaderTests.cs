using SyncFactors.Automation;

namespace SyncFactors.Automation.Tests;

public sealed class AutomationScenarioLoaderTests
{
    [Fact]
    public async Task LoadAsync_AcceptsValidScenarioAndTagFilter()
    {
        using var temp = new TempScenarioDirectory();
        var path = temp.Write(
            "scenario.json",
            """
            {
              "name": "valid",
              "tags": ["smoke", "real-ad"],
              "resetAdBeforeScenario": true,
              "resetMockBeforeScenario": true,
              "syncMode": "BulkSync",
              "iterations": [
                {
                  "order": 1,
                  "name": "create",
                  "mutations": [
                    {
                      "workerId": "10003",
                      "set": {
                        "company": "CORP"
                      }
                    }
                  ],
                  "expectation": {
                    "runStatus": "Succeeded",
                    "bucketCounts": {
                      "creates": 1
                    },
                    "workerOperations": [
                      {
                        "workerId": "10003",
                        "operations": ["CreateUser"]
                      }
                    ]
                  }
                }
              ],
              "finalExpectation": {
                "expectedAdUsers": [
                  {
                    "workerId": "10003",
                    "samAccountName": "10003",
                    "parentOu": "OU=Active,DC=example,DC=test",
                    "enabled": true,
                    "displayName": "Sample103, Casey",
                    "attributes": {
                      "employeeID": "10003"
                    }
                  }
                ]
              }
            }
            """);

        var scenarios = await ScenarioLoader.LoadAsync([path], new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "smoke" }, CancellationToken.None);

        var scenario = Assert.Single(scenarios);
        Assert.Equal("valid", scenario.Name);
        Assert.True(scenario.ResetAdBeforeScenario);
        Assert.Equal(AutomationRiskLevels.Safe, scenario.RiskLevel);
        Assert.Equal("BulkSync", scenario.SyncMode);
        Assert.Equal("10003", Assert.Single(scenario.FinalExpectation!.ExpectedAdUsers!).WorkerId);
    }

    [Fact]
    public async Task LoadAsync_RejectsIterationWithoutExpectation()
    {
        using var temp = new TempScenarioDirectory();
        var path = temp.Write(
            "missing-expectation.json",
            """
            {
              "name": "invalid",
              "tags": ["smoke"],
              "iterations": [
                {
                  "order": 1,
                  "mutations": []
                }
              ]
            }
            """);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            ScenarioLoader.LoadAsync([path], new HashSet<string>(StringComparer.OrdinalIgnoreCase), CancellationToken.None));

        Assert.Contains("missing expectation", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task LoadAsync_RejectsUnknownRiskLevel()
    {
        using var temp = new TempScenarioDirectory();
        var path = temp.Write(
            "unknown-risk.json",
            """
            {
              "name": "invalid-risk",
              "riskLevel": "prod",
              "iterations": [
                {
                  "order": 1,
                  "mutations": [],
                  "expectation": {
                    "runStatus": "Succeeded",
                    "bucketCounts": {},
                    "workerOperations": []
                  }
                }
              ]
            }
            """);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            ScenarioLoader.LoadAsync([path], new HashSet<string>(StringComparer.OrdinalIgnoreCase), CancellationToken.None));

        Assert.Contains("unsupported riskLevel", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Parse_UsesAutomationDefaultsAndEnvironmentCredentials()
    {
        var originalUsername = Environment.GetEnvironmentVariable("SYNCFACTORS_AUTOMATION_USERNAME");
        var originalPassword = Environment.GetEnvironmentVariable("SYNCFACTORS_AUTOMATION_PASSWORD");
        try
        {
            Environment.SetEnvironmentVariable("SYNCFACTORS_AUTOMATION_USERNAME", "operator");
            Environment.SetEnvironmentVariable("SYNCFACTORS_AUTOMATION_PASSWORD", "secret");

            var options = AutomationOptions.Parse(
            [
                "--scenario", "config/automation/*.json",
                "--tags", "real-ad,smoke",
                "--allow-ad-reset",
                "--api-url", "https://127.0.0.1:5087",
                "--mock-url", "http://127.0.0.1:18080"
            ]);

            Assert.Equal("operator", options.Username);
            Assert.Equal("secret", options.Password);
            Assert.True(options.AllowAdReset);
            Assert.False(options.IncludeDestructive);
            Assert.Contains("real-ad", options.Tags);
            Assert.Contains("smoke", options.Tags);
        }
        finally
        {
            Environment.SetEnvironmentVariable("SYNCFACTORS_AUTOMATION_USERNAME", originalUsername);
            Environment.SetEnvironmentVariable("SYNCFACTORS_AUTOMATION_PASSWORD", originalPassword);
        }
    }

    [Fact]
    public void RiskPolicy_RejectsDestructiveScenarioWithoutExplicitFlag()
    {
        var scenario = CreateScenario("destructive");
        var options = AutomationOptions.Parse(
        [
            "--scenario", "config/automation/*.json",
            "--username", "operator",
            "--password", "secret"
        ]);

        var exception = Assert.Throws<InvalidOperationException>(() => AutomationRiskPolicy.EnsureAllowed(scenario, options));
        Assert.Contains("--include-destructive", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void RiskPolicy_AllowsDestructiveScenarioWithExplicitFlag()
    {
        var scenario = CreateScenario("destructive");
        var options = AutomationOptions.Parse(
        [
            "--scenario", "config/automation/*.json",
            "--username", "operator",
            "--password", "secret",
            "--include-destructive"
        ]);

        AutomationRiskPolicy.EnsureAllowed(scenario, options);
    }

    [Fact]
    public void AdDiff_CatchesMissingUnexpectedOuEnabledAttributeAndDuplicates()
    {
        var expected = new[]
        {
            new AutomationExpectedAdUser(
                WorkerId: "10003",
                SamAccountName: "10003",
                ParentOu: "OU=Active,DC=example,DC=test",
                Enabled: true,
                DisplayName: "Expected User",
                Attributes: new Dictionary<string, string?> { ["company"] = "Expected" })
        };
        var actual = new AutomationAdSnapshot(
            Phase: "final",
            CapturedAtUtc: DateTimeOffset.UtcNow,
            TotalUsers: 3,
            Users:
            [
                new AutomationAdSnapshotUser(
                    OuName: "graveyard",
                    ParentOu: "OU=Graveyard,DC=example,DC=test",
                    WorkerId: "10003",
                    SamAccountName: "10003",
                    DistinguishedName: "CN=10003,OU=Graveyard,DC=example,DC=test",
                    Enabled: false,
                    DisplayName: "Actual User",
                    Attributes: new Dictionary<string, string?> { ["company"] = "Actual", ["mail"] = "duplicate@example.test" }),
                new AutomationAdSnapshotUser(
                    OuName: "active",
                    ParentOu: "OU=Active,DC=example,DC=test",
                    WorkerId: "10004",
                    SamAccountName: "10004",
                    DistinguishedName: "CN=10004,OU=Active,DC=example,DC=test",
                    Enabled: true,
                    DisplayName: "Unexpected User",
                    Attributes: new Dictionary<string, string?> { ["mail"] = "duplicate@example.test" })
            ],
            Warnings: []);

        var diffs = AutomationAdDiff.Build(expected, actual);

        Assert.Contains(diffs, diff => diff.Message.Contains("parent OU", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(diffs, diff => diff.Message.Contains("enabled", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(diffs, diff => diff.Message.Contains("company", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(diffs, diff => diff.Message.Contains("Unexpected AD user 10004", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(diffs, diff => diff.Message.Contains("Duplicate AD mail", StringComparison.OrdinalIgnoreCase));
    }

    private static AutomationScenario CreateScenario(string riskLevel) =>
        new(
            Name: "risk-test",
            Tags: ["real-ad"],
            RiskLevel: riskLevel,
            Preflight: null,
            ResetAdBeforeScenario: true,
            ResetMockBeforeScenario: true,
            SyncMode: "BulkSync",
            ExpectedDurationSeconds: null,
            ExpectedRunStatus: null,
            ExpectedQueueStatus: null,
            ExpectedHealth: null,
            ExpectedAuditEvents: [],
            Iterations:
            [
                new AutomationIteration(
                    Order: 1,
                    Name: "run",
                    Mutations: [],
                    Expectation: new AutomationIterationExpectation(
                        RunStatus: "Succeeded",
                        BucketCounts: new Dictionary<string, int>(),
                        WorkerOperations: []),
                    SourceAssertions: null,
                    RunAssertions: null,
                    AdAssertions: null,
                    ReportAssertions: null)
            ],
            FinalExpectation: null);

    private sealed class TempScenarioDirectory : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), $"syncfactors-automation-tests-{Guid.NewGuid():N}");

        public TempScenarioDirectory()
        {
            Directory.CreateDirectory(Root);
        }

        public string Write(string fileName, string content)
        {
            var path = Path.Combine(Root, fileName);
            File.WriteAllText(path, content);
            return path;
        }

        public void Dispose()
        {
            if (Directory.Exists(Root))
            {
                Directory.Delete(Root, recursive: true);
            }
        }
    }
}

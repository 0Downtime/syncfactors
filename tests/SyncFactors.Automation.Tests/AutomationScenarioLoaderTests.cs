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
            Assert.Contains("real-ad", options.Tags);
            Assert.Contains("smoke", options.Tags);
        }
        finally
        {
            Environment.SetEnvironmentVariable("SYNCFACTORS_AUTOMATION_USERNAME", originalUsername);
            Environment.SetEnvironmentVariable("SYNCFACTORS_AUTOMATION_PASSWORD", originalPassword);
        }
    }

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

using System.Text.Json;
using SyncFactors.Contracts;
using SyncFactors.Domain;
using SyncFactors.MockSuccessFactors;

namespace SyncFactors.MockSuccessFactors.Tests;

public sealed class LifecycleSimulationCommandTests
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true
    };
    private static readonly string ProjectRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));

    [Fact]
    public async Task RunAsync_CheckedInSimulationMasterSuite_CoversAllCheckedInScenarios()
    {
        var scenarios = new[]
        {
            new CheckedInSimulationCase(
                Name: "single",
                ScenarioPath: Path.Combine(ProjectRoot, "config", "mock-successfactors", "sample-lifecycle-scenario.json"),
                FixturePath: Path.Combine(ProjectRoot, "config", "mock-successfactors", "sample-lifecycle-fixtures.json"),
                ExpectedExitCode: 0),
            new CheckedInSimulationCase(
                Name: "multi",
                ScenarioPath: Path.Combine(ProjectRoot, "config", "mock-successfactors", "sample-lifecycle-multiuser-scenario.json"),
                FixturePath: Path.Combine(ProjectRoot, "config", "mock-successfactors", "sample-lifecycle-multiuser-fixtures.json"),
                ExpectedExitCode: 0),
            new CheckedInSimulationCase(
                Name: "population",
                ScenarioPath: Path.Combine(ProjectRoot, "config", "mock-successfactors", "sample-lifecycle-population-scenario.json"),
                FixturePath: Path.Combine(ProjectRoot, "config", "mock-successfactors", "sample-lifecycle-multiuser-fixtures.json"),
                ExpectedExitCode: 0),
            new CheckedInSimulationCase(
                Name: "failure",
                ScenarioPath: Path.Combine(ProjectRoot, "config", "mock-successfactors", "sample-lifecycle-failure-scenario.json"),
                FixturePath: Path.Combine(ProjectRoot, "config", "mock-successfactors", "sample-lifecycle-failure-fixtures.json"),
                ExpectedExitCode: 0),
            new CheckedInSimulationCase(
                Name: "expected-failure",
                ScenarioPath: Path.Combine(ProjectRoot, "config", "mock-successfactors", "sample-lifecycle-expected-failure.json"),
                FixturePath: Path.Combine(ProjectRoot, "config", "mock-successfactors", "sample-lifecycle-fixtures.json"),
                ExpectedExitCode: 1)
        };

        using var temp = new TempSimulationWorkspace();
        var failures = new List<string>();

        foreach (var simulationCase in scenarios)
        {
            var reportPath = Path.Combine(temp.Root, $"{simulationCase.Name}-master-report.md");
            using var output = new StringWriter();

            var exitCode = await LifecycleSimulationCommand.RunAsync(
                new LifecycleSimulationRequest(simulationCase.ScenarioPath, simulationCase.FixturePath, null, reportPath),
                output,
                CancellationToken.None);

            if (exitCode != simulationCase.ExpectedExitCode)
            {
                failures.Add($"case '{simulationCase.Name}' expected exit code {simulationCase.ExpectedExitCode} but found {exitCode}.");
            }

            if (!File.Exists(reportPath))
            {
                failures.Add($"case '{simulationCase.Name}' did not write markdown report '{reportPath}'.");
            }

            if (!File.Exists(Path.ChangeExtension(reportPath, ".json")))
            {
                failures.Add($"case '{simulationCase.Name}' did not write json report '{Path.ChangeExtension(reportPath, ".json")}'.");
            }

            var summary = output.ToString();
            if (!summary.Contains("Lifecycle Simulation Report", StringComparison.Ordinal))
            {
                failures.Add($"case '{simulationCase.Name}' did not emit the console summary header.");
            }

            if (simulationCase.ExpectedExitCode == 0 &&
                !summary.Contains("Result: PASSED", StringComparison.OrdinalIgnoreCase))
            {
                failures.Add($"case '{simulationCase.Name}' did not report a passing simulation summary.");
            }

            if (simulationCase.ExpectedExitCode != 0 &&
                !summary.Contains("Result: FAILED", StringComparison.OrdinalIgnoreCase))
            {
                failures.Add($"case '{simulationCase.Name}' did not report a failing simulation summary.");
            }
        }

        Assert.True(failures.Count == 0, string.Join(Environment.NewLine, failures));
    }

    [Fact]
    public void TryParse_AcceptsExpectedArguments()
    {
        var command = LifecycleSimulationCommand.TryParse(
        [
            "simulate-lifecycle",
            "--scenario", "/tmp/scenario.json",
            "--fixtures", "/tmp/fixtures.json",
            "--iterations", "2",
            "--report", "/tmp/report.json"
        ]);

        Assert.NotNull(command);
        Assert.Equal(Path.GetFullPath("/tmp/scenario.json"), command!.ScenarioPath);
        Assert.Equal(Path.GetFullPath("/tmp/fixtures.json"), command.FixturePath);
        Assert.Equal(2, command.Iterations);
        Assert.Equal(Path.GetFullPath("/tmp/report.json"), command.ReportPath);
    }

    [Fact]
    public async Task RunAsync_CheckedInMultiUserSample_Passes_AndWritesMarkdownAndJsonReports()
    {
        var scenarioPath = Path.Combine(ProjectRoot, "config", "mock-successfactors", "sample-lifecycle-multiuser-scenario.json");
        var fixturePath = Path.Combine(ProjectRoot, "config", "mock-successfactors", "sample-lifecycle-multiuser-fixtures.json");
        using var temp = new TempSimulationWorkspace();
        var reportPath = Path.Combine(temp.Root, "checked-in-sample-report.md");

        var exitCode = await LifecycleSimulationCommand.RunAsync(
            new LifecycleSimulationRequest(scenarioPath, fixturePath, null, reportPath),
            new StringWriter(),
            CancellationToken.None);

        Assert.Equal(0, exitCode);
        Assert.True(File.Exists(reportPath));
        Assert.True(File.Exists(Path.ChangeExtension(reportPath, ".json")));
        Assert.Contains("Lifecycle Simulation Report", await File.ReadAllTextAsync(reportPath), StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunAsync_CheckedInPopulationSample_Passes_AndWritesMarkdownAndJsonReports()
    {
        var scenarioPath = Path.Combine(ProjectRoot, "config", "mock-successfactors", "sample-lifecycle-population-scenario.json");
        var fixturePath = Path.Combine(ProjectRoot, "config", "mock-successfactors", "sample-lifecycle-multiuser-fixtures.json");
        using var temp = new TempSimulationWorkspace();
        var reportPath = Path.Combine(temp.Root, "checked-in-population-sample-report.md");

        var exitCode = await LifecycleSimulationCommand.RunAsync(
            new LifecycleSimulationRequest(scenarioPath, fixturePath, null, reportPath),
            new StringWriter(),
            CancellationToken.None);

        Assert.Equal(0, exitCode);
        Assert.True(File.Exists(reportPath));
        Assert.True(File.Exists(Path.ChangeExtension(reportPath, ".json")));
        Assert.Contains("Lifecycle Simulation Report", await File.ReadAllTextAsync(reportPath), StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunAsync_CheckedInFailureSample_Passes_AndWritesMarkdownAndJsonReports()
    {
        var scenarioPath = Path.Combine(ProjectRoot, "config", "mock-successfactors", "sample-lifecycle-failure-scenario.json");
        var fixturePath = Path.Combine(ProjectRoot, "config", "mock-successfactors", "sample-lifecycle-failure-fixtures.json");
        using var temp = new TempSimulationWorkspace();
        var reportPath = Path.Combine(temp.Root, "checked-in-failure-sample-report.md");

        var exitCode = await LifecycleSimulationCommand.RunAsync(
            new LifecycleSimulationRequest(scenarioPath, fixturePath, null, reportPath),
            new StringWriter(),
            CancellationToken.None);

        Assert.Equal(0, exitCode);
        Assert.True(File.Exists(reportPath));
        Assert.True(File.Exists(Path.ChangeExtension(reportPath, ".json")));
        Assert.Contains("Lifecycle Simulation Report", await File.ReadAllTextAsync(reportPath), StringComparison.Ordinal);
    }

    [Fact]
    public async Task StatefulDirectoryCommandGateway_AppliesCreateMoveDisableEnableAndUpdate()
    {
        var state = new LifecycleSimulationHarness.LifecycleSimulationDirectoryState([]);
        var gateway = new LifecycleSimulationHarness.StatefulDirectoryCommandGateway(state);

        var createCommand = new DirectoryMutationCommand(
            Action: "CreateUser",
            WorkerId: "10003",
            ManagerId: null,
            ManagerDistinguishedName: null,
            SamAccountName: "10003",
            CommonName: "10003",
            UserPrincipalName: "casey.sample103@Exampleenergy.com",
            Mail: "casey.sample103@Exampleenergy.com",
            TargetOu: "OU=Prehire,OU=ExampleQA-Users,DC=ExampleQA,DC=biz",
            DisplayName: "Sample103, Casey",
            CurrentDistinguishedName: null,
            EnableAccount: true,
            Operations: [new DirectoryOperation("CreateUser", "OU=Prehire,OU=ExampleQA-Users,DC=ExampleQA,DC=biz")],
            Attributes: new Dictionary<string, string?>
            {
                ["employeeID"] = "10003",
                ["company"] = "CORP",
                ["streetAddress"] = "103 Example Way"
            });

        await gateway.ExecuteAsync(createCommand, CancellationToken.None);

        var moveAndDisableCommand = createCommand with
        {
            Action = "MoveUser",
            TargetOu = "OU=GRAVEYARD,OU=ExampleQA-Users,DC=ExampleQA,DC=biz",
            Operations =
            [
                new DirectoryOperation("MoveUser", "OU=GRAVEYARD,OU=ExampleQA-Users,DC=ExampleQA,DC=biz"),
                new DirectoryOperation("DisableUser")
            ]
        };

        await gateway.ExecuteAsync(moveAndDisableCommand, CancellationToken.None);

        var enableAndUpdateCommand = createCommand with
        {
            Action = "EnableUser",
            TargetOu = "OU=POWERSHELL,OU=ExampleQA-Users,DC=ExampleQA,DC=biz",
            EnableAccount = true,
            Operations =
            [
                new DirectoryOperation("MoveUser", "OU=POWERSHELL,OU=ExampleQA-Users,DC=ExampleQA,DC=biz"),
                new DirectoryOperation("UpdateUser"),
                new DirectoryOperation("EnableUser")
            ],
            Attributes = new Dictionary<string, string?>
            {
                ["company"] = "CORP West",
                ["streetAddress"] = "300 Example Way"
            }
        };

        await gateway.ExecuteAsync(enableAndUpdateCommand, CancellationToken.None);

        var user = Assert.Single(state.ListUsers());
        Assert.Equal("10003", user.WorkerId);
        Assert.Equal("OU=POWERSHELL,OU=ExampleQA-Users,DC=ExampleQA,DC=biz", DirectoryDistinguishedName.GetParentOu(user.DistinguishedName));
        Assert.True(user.Enabled);
        Assert.Equal("CORP West", user.Attributes["company"]);
        Assert.Equal("300 Example Way", user.Attributes["streetAddress"]);
    }

    [Fact]
    public async Task StatefulDirectoryCommandGateway_RejectsDuplicateSamAccountNameOnCreate()
    {
        var state = new LifecycleSimulationHarness.LifecycleSimulationDirectoryState(
        [
            new LifecycleSimulationDirectoryUserInput(
                WorkerId: "90020",
                SamAccountName: "10020",
                ParentOu: "OU=POWERSHELL,OU=ExampleQA-Users,DC=ExampleQA,DC=biz",
                Enabled: true,
                DisplayName: "Conflict Owner, Existing",
                Attributes: new Dictionary<string, string?> { ["employeeID"] = "90020" })
        ]);
        var gateway = new LifecycleSimulationHarness.StatefulDirectoryCommandGateway(state);

        var createCommand = new DirectoryMutationCommand(
            Action: "CreateUser",
            WorkerId: "10020",
            ManagerId: null,
            ManagerDistinguishedName: null,
            SamAccountName: "10020",
            CommonName: "10020",
            UserPrincipalName: "taylor.conflict@Exampleenergy.com",
            Mail: "taylor.conflict@Exampleenergy.com",
            TargetOu: "OU=POWERSHELL,OU=ExampleQA-Users,DC=ExampleQA,DC=biz",
            DisplayName: "Conflict, Taylor",
            CurrentDistinguishedName: null,
            EnableAccount: true,
            Operations: [new DirectoryOperation("CreateUser", "OU=POWERSHELL,OU=ExampleQA-Users,DC=ExampleQA,DC=biz")],
            Attributes: new Dictionary<string, string?> { ["employeeID"] = "10020" });

        await Assert.ThrowsAsync<InvalidOperationException>(() => gateway.ExecuteAsync(createCommand, CancellationToken.None));
    }

    [Fact]
    public async Task RunAsync_EndToEndLifecycleScenario_PassesAndWritesReport()
    {
        using var temp = new TempSimulationWorkspace();
        var worker = GetFixtureWorker("10003") with
        {
            StartDate = "2099-04-02T00:00:00Z",
            LifecycleState = "preboarding",
            EmploymentStatus = "64300"
        };

        var scenarioPath = temp.WriteScenario(new LifecycleSimulationScenario(
            Name: "employee-lifecycle",
            RunSettings: null,
            InitialDirectoryUsers: [],
            Iterations:
            [
                new LifecycleSimulationIteration(
                    Order: 1,
                    Name: "preboarding-create",
                    Mutations: [],
                    Expectation: new IterationExpectation("Succeeded", new Dictionary<string, int> { ["creates"] = 1 }, [new ExpectedWorkerOperation("10003", ["CreateUser"])])),
                new LifecycleSimulationIteration(
                    Order: 2,
                    Name: "active-enable",
                    Mutations:
                    [
                        new WorkerMutation("10003", false, null, new Dictionary<string, string?> { ["startDate"] = "2026-03-15T00:00:00Z", ["lifecycleState"] = "active" })
                    ],
                    Expectation: new IterationExpectation("Succeeded", new Dictionary<string, int> { ["enables"] = 1 }, [new ExpectedWorkerOperation("10003", ["MoveUser"])])),
                new LifecycleSimulationIteration(
                    Order: 3,
                    Name: "mapped-update",
                    Mutations:
                    [
                        new WorkerMutation("10003", false, null, new Dictionary<string, string?> { ["company"] = "CORP West", ["locationAddress"] = "300 Example Way" })
                    ],
                    Expectation: new IterationExpectation("Succeeded", new Dictionary<string, int> { ["updates"] = 1 }, [new ExpectedWorkerOperation("10003", ["UpdateUser"])])),
                new LifecycleSimulationIteration(
                    Order: 4,
                    Name: "leave-disable",
                    Mutations:
                    [
                        new WorkerMutation("10003", false, null, new Dictionary<string, string?> { ["employmentStatus"] = "64303", ["emplStatus"] = "64303" })
                    ],
                    Expectation: new IterationExpectation("Succeeded", new Dictionary<string, int> { ["disables"] = 1 }, [new ExpectedWorkerOperation("10003", ["MoveUser", "DisableUser"])])),
                new LifecycleSimulationIteration(
                    Order: 5,
                    Name: "active-reenable",
                    Mutations:
                    [
                        new WorkerMutation("10003", false, null, new Dictionary<string, string?> { ["employmentStatus"] = "64300", ["emplStatus"] = "64300" })
                    ],
                    Expectation: new IterationExpectation("Succeeded", new Dictionary<string, int> { ["enables"] = 1 }, [new ExpectedWorkerOperation("10003", ["MoveUser", "EnableUser"])])),
                new LifecycleSimulationIteration(
                    Order: 6,
                    Name: "terminated-graveyard",
                    Mutations:
                    [
                        new WorkerMutation("10003", false, null, new Dictionary<string, string?>
                        {
                            ["employmentStatus"] = "64308",
                            ["emplStatus"] = "64308",
                            ["endDate"] = "2026-04-01T00:00:00Z",
                            ["lifecycleState"] = "terminated"
                        })
                    ],
                    Expectation: new IterationExpectation("Succeeded", new Dictionary<string, int> { ["graveyardMoves"] = 1 }, [new ExpectedWorkerOperation("10003", ["MoveUser", "DisableUser"])]))
            ],
            FinalExpectation: new FinalDirectoryExpectation(
                RequireExactUserSet: true,
                DirectoryUsers:
                [
                    new ExpectedDirectoryUser(
                        WorkerId: "10003",
                        SamAccountName: "10003",
                        ParentOu: "OU=GRAVEYARD,OU=ExampleQA-Users,DC=ExampleQA,DC=biz",
                        Enabled: false,
                        DisplayName: "Sample103, Casey",
                        Attributes: new Dictionary<string, string?>
                        {
                            ["employeeID"] = "10003",
                            ["company"] = "CORP West",
                            ["streetAddress"] = "300 Example Way"
                        })
                ])));
        var fixturePath = temp.WriteFixtures(worker);
        var reportPath = Path.Combine(temp.Root, "report.json");
        var output = new StringWriter();

        var exitCode = await LifecycleSimulationCommand.RunAsync(
            new LifecycleSimulationRequest(scenarioPath, fixturePath, Iterations: null, ReportPath: reportPath),
            output,
            CancellationToken.None);

        Assert.Equal(0, exitCode);
        Assert.Contains("Result: PASSED", output.ToString(), StringComparison.OrdinalIgnoreCase);
        Assert.True(File.Exists(reportPath));
        Assert.True(File.Exists(Path.ChangeExtension(reportPath, ".md")));

        var report = JsonSerializer.Deserialize<LifecycleSimulationReport>(await File.ReadAllTextAsync(reportPath), JsonOptions);
        Assert.NotNull(report);
        Assert.True(report!.Passed);
        Assert.Equal(6, report.Iterations.Count);
        Assert.Empty(report.Failures);
    }

    [Fact]
    public async Task RunAsync_ReturnsNonZero_WhenBucketExpectationIsWrong()
    {
        using var temp = new TempSimulationWorkspace();
        var scenarioPath = temp.WriteScenario(new LifecycleSimulationScenario(
            Name: "wrong-bucket",
            RunSettings: null,
            InitialDirectoryUsers: [],
            Iterations:
            [
                new LifecycleSimulationIteration(
                    Order: 1,
                    Name: "create",
                    Mutations: [],
                    Expectation: new IterationExpectation("Succeeded", new Dictionary<string, int> { ["creates"] = 0 }, [new ExpectedWorkerOperation("10003", ["CreateUser"])]))
            ],
            FinalExpectation: new FinalDirectoryExpectation(false, [])));
        var fixturePath = temp.WriteFixtures(GetFixtureWorker("10003"));
        var output = new StringWriter();

        var exitCode = await LifecycleSimulationCommand.RunAsync(
            new LifecycleSimulationRequest(scenarioPath, fixturePath, null, null),
            output,
            CancellationToken.None);

        Assert.Equal(1, exitCode);
        Assert.Contains("expected bucket 'creates' to equal 0 but found 1", output.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task RunAsync_ReturnsNonZero_ForUnknownWorkerReference()
    {
        using var temp = new TempSimulationWorkspace();
        var scenarioPath = temp.WriteScenario(new LifecycleSimulationScenario(
            Name: "unknown-worker",
            RunSettings: null,
            InitialDirectoryUsers: [],
            Iterations:
            [
                new LifecycleSimulationIteration(
                    Order: 1,
                    Name: "bad-mutation",
                    Mutations:
                    [
                        new WorkerMutation("missing-worker", false, null, new Dictionary<string, string?> { ["company"] = "CORP West" })
                    ],
                    Expectation: new IterationExpectation("Succeeded", new Dictionary<string, int>(), []))
            ],
            FinalExpectation: new FinalDirectoryExpectation(false, [])));
        var fixturePath = temp.WriteFixtures(GetFixtureWorker("10003"));

        var exitCode = await LifecycleSimulationCommand.RunAsync(
            new LifecycleSimulationRequest(scenarioPath, fixturePath, null, null),
            new StringWriter(),
            CancellationToken.None);

        Assert.Equal(1, exitCode);
    }

    [Fact]
    public async Task RunAsync_ReturnsNonZero_ForUnsupportedMutationField()
    {
        using var temp = new TempSimulationWorkspace();
        var scenarioPath = temp.WriteScenario(new LifecycleSimulationScenario(
            Name: "unsupported-field",
            RunSettings: null,
            InitialDirectoryUsers: [],
            Iterations:
            [
                new LifecycleSimulationIteration(
                    Order: 1,
                    Name: "bad-field",
                    Mutations:
                    [
                        new WorkerMutation("10003", false, null, new Dictionary<string, string?> { ["unsupportedField"] = "value" })
                    ],
                    Expectation: new IterationExpectation("Succeeded", new Dictionary<string, int>(), []))
            ],
            FinalExpectation: new FinalDirectoryExpectation(false, [])));
        var fixturePath = temp.WriteFixtures(GetFixtureWorker("10003"));

        var exitCode = await LifecycleSimulationCommand.RunAsync(
            new LifecycleSimulationRequest(scenarioPath, fixturePath, null, null),
            new StringWriter(),
            CancellationToken.None);

        Assert.Equal(1, exitCode);
    }

    [Fact]
    public async Task RunAsync_ReturnsNonZero_ForInvalidIterationOrder()
    {
        using var temp = new TempSimulationWorkspace();
        var scenarioPath = temp.WriteScenario(new LifecycleSimulationScenario(
            Name: "invalid-order",
            RunSettings: null,
            InitialDirectoryUsers: [],
            Iterations:
            [
                new LifecycleSimulationIteration(
                    Order: 2,
                    Name: "out-of-order",
                    Mutations: [],
                    Expectation: new IterationExpectation("Succeeded", new Dictionary<string, int>(), []))
            ],
            FinalExpectation: new FinalDirectoryExpectation(false, [])));
        var fixturePath = temp.WriteFixtures(GetFixtureWorker("10003"));

        var exitCode = await LifecycleSimulationCommand.RunAsync(
            new LifecycleSimulationRequest(scenarioPath, fixturePath, null, null),
            new StringWriter(),
            CancellationToken.None);

        Assert.Equal(1, exitCode);
    }

    private static MockWorkerFixture GetFixtureWorker(string workerId)
    {
        var fixturePath = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory,
            "..",
            "..",
            "..",
            "..",
            "..",
            "config",
            "mock-successfactors",
            "baseline-fixtures.json"));
        var document = JsonSerializer.Deserialize<MockFixtureDocument>(File.ReadAllText(fixturePath), JsonOptions)
            ?? throw new InvalidOperationException("Fixture document could not be loaded.");

        return document.Workers.Single(worker => string.Equals(worker.PersonIdExternal, workerId, StringComparison.OrdinalIgnoreCase));
    }

    private sealed class TempSimulationWorkspace : IDisposable
    {
        public TempSimulationWorkspace()
        {
            Root = Path.Combine(Path.GetTempPath(), "syncfactors-lifecycle-tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Root);
        }

        public string Root { get; }

        public string WriteScenario(LifecycleSimulationScenario scenario)
        {
            var path = Path.Combine(Root, $"{scenario.Name ?? "scenario"}.json");
            File.WriteAllText(path, JsonSerializer.Serialize(scenario, JsonOptions));
            return path;
        }

        public string WriteFixtures(params MockWorkerFixture[] workers)
        {
            var path = Path.Combine(Root, "fixtures.json");
            File.WriteAllText(path, JsonSerializer.Serialize(new MockFixtureDocument(workers), JsonOptions));
            return path;
        }

        public void Dispose()
        {
            try
            {
                Directory.Delete(Root, recursive: true);
            }
            catch
            {
            }
        }
    }

    private sealed record CheckedInSimulationCase(
        string Name,
        string ScenarioPath,
        string FixturePath,
        int ExpectedExitCode);
}

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
            TargetOu: "OU=Prehire,DC=example,DC=com",
            DisplayName: "Sample103, Casey",
            CurrentDistinguishedName: null,
            EnableAccount: false,
            Operations: [new DirectoryOperation("CreateUser", "OU=Prehire,DC=example,DC=com")],
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
            TargetOu = "OU=LabGraveyard,DC=example,DC=com",
            Operations =
            [
                new DirectoryOperation("MoveUser", "OU=LabGraveyard,DC=example,DC=com"),
                new DirectoryOperation("DisableUser")
            ]
        };

        await gateway.ExecuteAsync(moveAndDisableCommand, CancellationToken.None);

        var enableAndUpdateCommand = createCommand with
        {
            Action = "EnableUser",
            TargetOu = "OU=LabUsers,DC=example,DC=com",
            EnableAccount = true,
            Operations =
            [
                new DirectoryOperation("MoveUser", "OU=LabUsers,DC=example,DC=com"),
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
        Assert.Equal("OU=LabUsers,DC=example,DC=com", DirectoryDistinguishedName.GetParentOu(user.DistinguishedName));
        Assert.True(user.Enabled);
        Assert.Equal("CORP West", user.Attributes["company"]);
        Assert.Equal("300 Example Way", user.Attributes["streetAddress"]);
    }

    [Fact]
    public async Task RunAsync_EndToEndLifecycleScenario_PassesAndWritesReport()
    {
        using var temp = new TempSimulationWorkspace();
        var worker = GetFixtureWorker("10003") with
        {
            StartDate = "2099-04-02T00:00:00Z",
            LifecycleState = "preboarding",
            EmploymentStatus = "A"
        };

        var scenarioPath = temp.WriteScenario(new LifecycleSimulationScenario(
            Name: "employee-lifecycle",
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
                    Expectation: new IterationExpectation("Succeeded", new Dictionary<string, int> { ["enables"] = 1 }, [new ExpectedWorkerOperation("10003", ["MoveUser", "EnableUser"])])),
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
                        new WorkerMutation("10003", false, null, new Dictionary<string, string?> { ["employmentStatus"] = "U", ["emplStatus"] = "U" })
                    ],
                    Expectation: new IterationExpectation("Succeeded", new Dictionary<string, int> { ["disables"] = 1 }, [new ExpectedWorkerOperation("10003", ["DisableUser"])])),
                new LifecycleSimulationIteration(
                    Order: 5,
                    Name: "active-reenable",
                    Mutations:
                    [
                        new WorkerMutation("10003", false, null, new Dictionary<string, string?> { ["employmentStatus"] = "A", ["emplStatus"] = "A" })
                    ],
                    Expectation: new IterationExpectation("Succeeded", new Dictionary<string, int> { ["enables"] = 1 }, [new ExpectedWorkerOperation("10003", ["EnableUser"])])),
                new LifecycleSimulationIteration(
                    Order: 6,
                    Name: "terminated-graveyard",
                    Mutations:
                    [
                        new WorkerMutation("10003", false, null, new Dictionary<string, string?>
                        {
                            ["employmentStatus"] = "T",
                            ["emplStatus"] = "T",
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
                        ParentOu: "OU=LabGraveyard,DC=example,DC=com",
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
        Assert.Contains("passed=True", output.ToString(), StringComparison.OrdinalIgnoreCase);
        Assert.True(File.Exists(reportPath));

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
}

using System.Text.Json;
using SyncFactors.MockSuccessFactors;

namespace SyncFactors.MockSuccessFactors.Tests;

public sealed class FixtureGenerationCommandTests
{
    [Theory]
    [InlineData(null, "active")]
    [InlineData("", "active")]
    [InlineData(" prehire ", "preboarding")]
    [InlineData("preboarding", "preboarding")]
    [InlineData("paid-leave", "paid-leave")]
    [InlineData("unpaid-leave", "unpaid-leave")]
    [InlineData("retired", "retired")]
    [InlineData("terminated", "terminated")]
    [InlineData("unexpected", "active")]
    public void MockLifecycleState_Normalize_MapsSupportedAliases(string? rawState, string expected)
    {
        Assert.Equal(expected, MockLifecycleState.Normalize(rawState));
    }

    [Theory]
    [InlineData("64304", null, "paid-leave")]
    [InlineData("U", null, "paid-leave")]
    [InlineData("64303", null, "unpaid-leave")]
    [InlineData("64307", null, "retired")]
    [InlineData("R", null, "retired")]
    [InlineData("64308", null, "terminated")]
    [InlineData("T", null, "terminated")]
    [InlineData("I", null, "terminated")]
    [InlineData(null, "2026-01-01", "terminated")]
    [InlineData(null, null, "active")]
    public void MockLifecycleState_Infer_MapsEmploymentStatusAndEndDates(
        string? employmentStatus,
        string? endDate,
        string expected)
    {
        Assert.Equal(expected, MockLifecycleState.Infer("2020-01-01", employmentStatus, endDate));
    }

    [Fact]
    public void MockLifecycleState_Infer_PrefersPrehireTagsAndActiveEndDateOverride()
    {
        Assert.Equal("preboarding", MockLifecycleState.Infer("2020-01-01", "64300", null, ["prehire"]));
        Assert.Equal("preboarding", MockLifecycleState.Infer(DateTimeOffset.UtcNow.AddDays(5).ToString("O"), "64300", null));
        Assert.Equal("active", MockLifecycleState.Infer("2020-01-01", null, "2026-01-01", ["active"]));
        Assert.False(MockLifecycleState.IsFutureDate("not-a-date"));
    }

    [Fact]
    public void MockFixtureSummaryReporter_DescribesAllProvisioningBucketsAndEmptySummary()
    {
        var labels = new Dictionary<string, string>
        {
            ["creates"] = "Create",
            ["updates"] = "Update",
            ["enables"] = "Enable",
            ["disables"] = "Disable",
            ["graveyardMoves"] = "Move To Graveyard",
            ["deletions"] = "Delete",
            ["manualReview"] = "Manual Review",
            ["quarantined"] = "Quarantined",
            ["conflicts"] = "Conflict",
            ["guardrailFailures"] = "Guardrail Failure",
            ["unchanged"] = "No Change",
            ["custom"] = "custom"
        };
        var output = new StringWriter();

        foreach (var pair in labels)
        {
            Assert.Equal(pair.Value, MockFixtureSummaryReporter.DescribeProvisioningBucket(pair.Key));
        }

        MockFixtureSummaryReporter.WriteSummary(output, new MockFixtureDocument([]), "empty");

        var summary = output.ToString();
        Assert.Contains("workers=0", summary);
        Assert.Contains("lifecycleTypes none", summary);
        Assert.Contains("provisioningBuckets none", summary);
        Assert.Contains("scenarioTags none", summary);
    }

    [Fact]
    public void MockFixtureSummaryReporter_InfersProvisioningBucketsFromWorkerState()
    {
        var active = MinimalWorker() with { ScenarioTags = ["create"] };
        var update = MinimalWorker() with { ScenarioTags = [] };
        var manual = MinimalWorker() with { ScenarioTags = ["missing-required-attribute"] };
        var leave = MinimalWorker() with { EmploymentStatus = "64304" };
        var terminated = MinimalWorker() with { EmploymentStatus = "64308" };

        Assert.Equal("creates", MockFixtureSummaryReporter.InferProvisioningBucket(active));
        Assert.Equal("updates", MockFixtureSummaryReporter.InferProvisioningBucket(update));
        Assert.Equal("manualReview", MockFixtureSummaryReporter.InferProvisioningBucket(manual));
        Assert.Equal("disables", MockFixtureSummaryReporter.InferProvisioningBucket(leave));
        Assert.Equal("graveyardMoves", MockFixtureSummaryReporter.InferProvisioningBucket(terminated));
    }

    [Fact]
    public void TryParse_RejectsNonCommandAndInvalidArguments()
    {
        Assert.Null(FixtureGenerationCommand.TryParse([]));
        Assert.Null(FixtureGenerationCommand.TryParse(["serve"]));
        Assert.Throws<InvalidOperationException>(() => FixtureGenerationCommand.TryParse(["generate-fixtures", "--input"]));
        Assert.Throws<InvalidOperationException>(() => FixtureGenerationCommand.TryParse(["generate-fixtures", "--unsupported", "value"]));
        Assert.Throws<InvalidOperationException>(() => FixtureGenerationCommand.TryParse(["generate-fixtures", "--input", "/tmp/input.json"]));

        var parsed = FixtureGenerationCommand.TryParse(
        [
            "generate-fixtures",
            "--input", "/tmp/input.json",
            "--output", "/tmp/output.json",
            "--manifest", "/tmp/manifest.json"
        ]);

        Assert.NotNull(parsed);
        Assert.Equal(Path.GetFullPath("/tmp/input.json"), parsed!.InputPath);
        Assert.Equal(Path.GetFullPath("/tmp/output.json"), parsed.OutputPath);
        Assert.Equal(Path.GetFullPath("/tmp/manifest.json"), parsed.ManifestPath);
    }

    [Fact]
    public async Task GenerateFixtures_ProducesDeterministicSanitizedOutput_AndManifest()
    {
        var tempDirectory = Path.Combine(Path.GetTempPath(), "syncfactors-mock-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectory);
        var inputPath = Path.Combine(tempDirectory, "input.json");
        var outputPath = Path.Combine(tempDirectory, "fixtures.json");
        var manifestPath = Path.Combine(tempDirectory, "manifest.json");
        var outputWriter = new StringWriter();
        File.Copy(Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "config", "mock-successfactors", "sample-export.json")), inputPath);

        var exitCode = await FixtureGenerationCommand.RunAsync(
            new FixtureGenerationRequest(inputPath, outputPath, manifestPath),
            outputWriter,
            CancellationToken.None);

        Assert.Equal(0, exitCode);
        var commandOutput = outputWriter.ToString();
        Assert.Contains("Mock fixture summary (generated fixtures)", commandOutput);
        Assert.Contains("workers=1", commandOutput);
        Assert.Contains("provisioningBuckets", commandOutput);
        Assert.Contains("Generated 1 sanitized fixtures", commandOutput);

        using var fixtureDocument = JsonDocument.Parse(await File.ReadAllTextAsync(outputPath));
        var worker = fixtureDocument.RootElement.GetProperty("workers")[0];
        var workerId = worker.GetProperty("personIdExternal").GetString()!;
        var expectedName = MockNameCatalog.GetNameProfile(int.Parse(workerId) - 10_000, includePreferredName: true);
        Assert.Matches("^\\d{5}$", worker.GetProperty("personIdExternal").GetString()!);
        Assert.Matches("^\\d{5}$", worker.GetProperty("personId").GetString()!);
        Assert.StartsWith("uuid-", worker.GetProperty("perPersonUuid").GetString());
        Assert.EndsWith("@example.test", worker.GetProperty("email").GetString());
        Assert.Equal("B", worker.GetProperty("emailType").GetString());
        Assert.Equal(expectedName.FirstName, worker.GetProperty("firstName").GetString());
        Assert.Equal(expectedName.LastName, worker.GetProperty("lastName").GetString());
        Assert.Equal(expectedName.PreferredName, worker.GetProperty("preferredName").GetString());
        Assert.StartsWith("Department-", worker.GetProperty("department").GetString());
        Assert.StartsWith("DepartmentName-", worker.GetProperty("departmentName").GetString());
        Assert.StartsWith("COMP-", worker.GetProperty("companyId").GetString());
        Assert.StartsWith("BU-", worker.GetProperty("businessUnitId").GetString());
        Assert.StartsWith("CC-", worker.GetProperty("costCenterId").GetString());
        Assert.Equal("US", worker.GetProperty("twoCharCountryCode").GetString());
        Assert.Equal("1", worker.GetProperty("activeEmploymentsCount").GetString());
        Assert.Equal("active", worker.GetProperty("lifecycleState").GetString());
        Assert.NotNull(worker.GetProperty("businessPhoneNumber").GetString());
        Assert.NotNull(worker.GetProperty("cellPhoneNumber").GetString());
        Assert.StartsWith("Floor ", worker.GetProperty("location").GetProperty("customString4").GetString());

        using var manifestDocument = JsonDocument.Parse(await File.ReadAllTextAsync(manifestPath));
        Assert.Equal(1, manifestDocument.RootElement.GetProperty("workerCount").GetInt32());
    }

    [Fact]
    public async Task GenerateFixtures_AcceptsValuePayloadAndFallbackFields()
    {
        var tempDirectory = Path.Combine(Path.GetTempPath(), "syncfactors-mock-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectory);
        var inputPath = Path.Combine(tempDirectory, "input.json");
        var outputPath = Path.Combine(tempDirectory, "fixtures.json");
        var outputWriter = new StringWriter();
        await File.WriteAllTextAsync(inputPath, """
        {
          "value": [
            {
              "personIdExternal": "source-worker-1",
              "personId": "person-1",
              "perPersonUuid": "uuid-source-1",
              "username": "source-user",
              "userId": "source-user-id",
              "email": "source@example.test",
              "firstName": "Source",
              "lastName": "Worker",
              "startDate": "2026-01-01T00:00:00Z",
              "managerId": "source-manager",
              "emplStatus": "64308",
              "endDate": "2026-02-01T00:00:00Z",
              "department": "Raw Department",
              "company": "Raw Company",
              "lastModifiedDateTime": "2026-02-02T00:00:00Z"
            }
          ]
        }
        """);

        var exitCode = await FixtureGenerationCommand.RunAsync(
            new FixtureGenerationRequest(inputPath, outputPath, ManifestPath: null),
            outputWriter,
            CancellationToken.None);

        Assert.Equal(0, exitCode);
        using var fixtureDocument = JsonDocument.Parse(await File.ReadAllTextAsync(outputPath));
        var worker = fixtureDocument.RootElement.GetProperty("workers")[0];
        Assert.Matches("^\\d{5}$", worker.GetProperty("personIdExternal").GetString()!);
        Assert.Equal("active", worker.GetProperty("lifecycleState").GetString());
        Assert.Null(worker.GetProperty("department").GetString());
        Assert.Null(worker.GetProperty("company").GetString());
        Assert.DoesNotContain("manifest", outputWriter.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task GenerateFixtures_UnsupportedPayloadWritesEmptyDocument()
    {
        var tempDirectory = Path.Combine(Path.GetTempPath(), "syncfactors-mock-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectory);
        var inputPath = Path.Combine(tempDirectory, "input.json");
        var outputPath = Path.Combine(tempDirectory, "fixtures.json");
        await File.WriteAllTextAsync(inputPath, """{ "unexpected": [] }""");

        var exitCode = await FixtureGenerationCommand.RunAsync(
            new FixtureGenerationRequest(inputPath, outputPath, ManifestPath: null),
            new StringWriter(),
            CancellationToken.None);

        Assert.Equal(0, exitCode);
        using var fixtureDocument = JsonDocument.Parse(await File.ReadAllTextAsync(outputPath));
        Assert.Empty(fixtureDocument.RootElement.GetProperty("workers").EnumerateArray());
    }

    [Fact]
    public async Task BaselineFixtures_CoverLifecycleMatrixTags()
    {
        using var document = JsonDocument.Parse(await File.ReadAllTextAsync(Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "config", "mock-successfactors", "baseline-fixtures.json"))));
        var workers = document.RootElement.GetProperty("workers").EnumerateArray().ToArray();
        var tags = workers
            .SelectMany(worker => worker.GetProperty("scenarioTags").EnumerateArray().Select(tag => tag.GetString()))
            .Where(tag => tag is not null)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var lifecycleStates = workers
            .Select(worker => worker.GetProperty("lifecycleState").GetString())
            .Where(state => state is not null)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        Assert.Contains("create", tags);
        Assert.Contains("update", tags);
        Assert.Contains("preboarding", tags);
        Assert.Contains("prehire", tags);
        Assert.Contains("leave", tags);
        Assert.Contains("inactive", tags);
        Assert.Contains("retired", tags);
        Assert.Contains("terminated", tags);
        Assert.Contains("manager-change", tags);
        Assert.Contains("ou-routing", tags);
        Assert.Contains("disable-candidate", tags);
        Assert.Contains("delete-candidate", tags);
        Assert.Contains("stale-termination", tags);
        Assert.Contains("missing-required-attribute", tags);
        Assert.Contains("review-case", tags);

        Assert.Contains("active", lifecycleStates);
        Assert.Contains("preboarding", lifecycleStates);
        Assert.Contains("paid-leave", lifecycleStates);
        Assert.Contains("unpaid-leave", lifecycleStates);
        Assert.Contains("retired", lifecycleStates);
        Assert.Contains("terminated", lifecycleStates);
    }

    [Fact]
    public async Task BaselineFixtures_SummaryIncludesProvisioningBuckets()
    {
        var fixturePath = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "config", "mock-successfactors", "baseline-fixtures.json"));
        using var document = JsonDocument.Parse(await File.ReadAllTextAsync(fixturePath));
        var workers = document.RootElement.GetProperty("workers")
            .EnumerateArray()
            .Select(worker => JsonSerializer.Deserialize<MockWorkerFixture>(worker.GetRawText()))
            .Where(worker => worker is not null)
            .Cast<MockWorkerFixture>()
            .ToArray();
        var fixtureDocument = new MockFixtureDocument(workers);
        var output = new StringWriter();

        MockFixtureSummaryReporter.WriteSummary(output, fixtureDocument, "test");

        var summary = output.ToString();
        Assert.Contains("workers=10", summary);
        Assert.Contains("lifecycleTypes active=4, preboarding=1, paid-leave=1, unpaid-leave=1, retired=1, terminated=2", summary);
        Assert.Contains("provisioningBuckets creates=2, updates=2, disables=2, graveyardMoves=3, manualReview=1", summary);
    }

    private static MockWorkerFixture MinimalWorker()
    {
        return new MockWorkerFixture(
            PersonIdExternal: "10000",
            UserName: "10000",
            Email: "user@example.test",
            FirstName: "Test",
            LastName: "Worker",
            StartDate: "2020-01-01",
            Department: null,
            Company: null,
            Location: null,
            JobTitle: null,
            BusinessUnit: null,
            Division: null,
            CostCenter: null,
            EmployeeClass: null,
            EmployeeType: null,
            ManagerId: null,
            PeopleGroup: null,
            LeadershipLevel: null,
            Region: null,
            Geozone: null,
            BargainingUnit: null,
            UnionJobCode: null,
            CintasUniformCategory: null,
            CintasUniformAllotment: null,
            EmploymentStatus: "64300",
            LifecycleState: null,
            EndDate: null,
            FirstDateWorked: null,
            LastDateWorked: null,
            IsContingentWorker: null,
            LastModifiedDateTime: null,
            ScenarioTags: [],
            Response: null,
            PersonId: "10000",
            PerPersonUuid: "uuid-10000",
            PreferredName: null,
            DisplayName: null,
            UserId: "10000",
            EmailType: null,
            DepartmentName: null,
            DepartmentId: null,
            DepartmentCostCenter: null,
            CompanyId: null,
            BusinessUnitId: null,
            DivisionId: null,
            CostCenterDescription: null,
            CostCenterId: null,
            TwoCharCountryCode: null,
            Position: null,
            PayGrade: null,
            BusinessPhoneNumber: null,
            BusinessPhoneAreaCode: null,
            BusinessPhoneCountryCode: null,
            BusinessPhoneExtension: null,
            CellPhoneNumber: null,
            CellPhoneAreaCode: null,
            CellPhoneCountryCode: null,
            ActiveEmploymentsCount: null,
            LatestTerminationDate: null);
    }
}

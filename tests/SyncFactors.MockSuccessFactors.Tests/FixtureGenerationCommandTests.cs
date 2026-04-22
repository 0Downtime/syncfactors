using System.Text.Json;
using SyncFactors.MockSuccessFactors;

namespace SyncFactors.MockSuccessFactors.Tests;

public sealed class FixtureGenerationCommandTests
{
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
}

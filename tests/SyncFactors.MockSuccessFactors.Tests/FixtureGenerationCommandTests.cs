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
        File.Copy(Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "config", "mock-successfactors", "sample-export.json")), inputPath);

        var exitCode = await FixtureGenerationCommand.RunAsync(
            new FixtureGenerationRequest(inputPath, outputPath, manifestPath),
            TextWriter.Null,
            CancellationToken.None);

        Assert.Equal(0, exitCode);

        using var fixtureDocument = JsonDocument.Parse(await File.ReadAllTextAsync(outputPath));
        var worker = fixtureDocument.RootElement.GetProperty("workers")[0];
        Assert.Matches("^\\d{5}$", worker.GetProperty("personIdExternal").GetString()!);
        Assert.Matches("^\\d{5}$", worker.GetProperty("personId").GetString()!);
        Assert.StartsWith("uuid-", worker.GetProperty("perPersonUuid").GetString());
        Assert.EndsWith("@example.test", worker.GetProperty("email").GetString());
        Assert.Equal("B", worker.GetProperty("emailType").GetString());
        Assert.DoesNotContain("Jane", worker.GetProperty("firstName").GetString(), StringComparison.OrdinalIgnoreCase);
        Assert.StartsWith("Preferred", worker.GetProperty("preferredName").GetString());
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
}

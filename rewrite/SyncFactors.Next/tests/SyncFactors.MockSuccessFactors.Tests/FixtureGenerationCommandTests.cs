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
        Assert.StartsWith("mock-", worker.GetProperty("personIdExternal").GetString());
        Assert.EndsWith("@example.test", worker.GetProperty("email").GetString());
        Assert.DoesNotContain("Jane", worker.GetProperty("firstName").GetString(), StringComparison.OrdinalIgnoreCase);
        Assert.StartsWith("Department-", worker.GetProperty("department").GetString());

        using var manifestDocument = JsonDocument.Parse(await File.ReadAllTextAsync(manifestPath));
        Assert.Equal(1, manifestDocument.RootElement.GetProperty("workerCount").GetInt32());
    }

    [Fact]
    public async Task BaselineFixtures_CoverLifecycleMatrixTags()
    {
        using var document = JsonDocument.Parse(await File.ReadAllTextAsync(Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "config", "mock-successfactors", "baseline-fixtures.json"))));
        var tags = document.RootElement.GetProperty("workers")
            .EnumerateArray()
            .SelectMany(worker => worker.GetProperty("scenarioTags").EnumerateArray().Select(tag => tag.GetString()))
            .Where(tag => tag is not null)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        Assert.Contains("create", tags);
        Assert.Contains("update", tags);
        Assert.Contains("prehire", tags);
        Assert.Contains("manager-change", tags);
        Assert.Contains("ou-routing", tags);
        Assert.Contains("disable-candidate", tags);
        Assert.Contains("delete-candidate", tags);
        Assert.Contains("missing-required-attribute", tags);
        Assert.Contains("review-case", tags);
    }
}

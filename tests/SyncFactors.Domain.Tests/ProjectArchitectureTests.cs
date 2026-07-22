using System.Xml.Linq;

namespace SyncFactors.Domain.Tests;

public sealed class ProjectArchitectureTests
{
    [Fact]
    public void Domain_abstractions_do_not_supply_silent_adapter_defaults()
    {
        var abstractions = File.ReadAllText(Path.Combine(GetRepositoryRoot(), "src", "SyncFactors.Domain", "Abstractions.cs"));

        Assert.DoesNotContain("Task.FromResult", abstractions);
    }

    [Fact]
    public void Production_projects_follow_the_contracts_domain_infrastructure_dependency_direction()
    {
        var root = GetRepositoryRoot();

        AssertProjectReferences(root, "SyncFactors.Contracts", []);
        AssertProjectReferences(root, "SyncFactors.Domain", ["SyncFactors.Contracts"]);
        AssertProjectReferences(root, "SyncFactors.Infrastructure", ["SyncFactors.Contracts", "SyncFactors.Domain"]);
        AssertProjectReferences(root, "SyncFactors.Api", ["SyncFactors.Contracts", "SyncFactors.Domain", "SyncFactors.Infrastructure"]);
        AssertProjectReferences(root, "SyncFactors.Worker", ["SyncFactors.Contracts", "SyncFactors.Domain", "SyncFactors.Infrastructure"]);
    }

    private static void AssertProjectReferences(string root, string projectName, IReadOnlyList<string> expected)
    {
        var projectFile = Path.Combine(root, "src", projectName, $"{projectName}.csproj");
        var actual = XDocument.Load(projectFile)
            .Descendants("ProjectReference")
            .Select(reference => reference.Attribute("Include")!.Value.Replace('\\', '/').Split('/')[^1])
            .Select(Path.GetFileNameWithoutExtension)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(expected.OrderBy(name => name, StringComparer.Ordinal), actual);
    }

    private static string GetRepositoryRoot()
    {
        foreach (var start in new[] { Directory.GetCurrentDirectory(), AppContext.BaseDirectory })
        {
            for (var directory = new DirectoryInfo(start); directory is not null; directory = directory.Parent)
            {
                if (File.Exists(Path.Combine(directory.FullName, "SyncFactors.Next.sln")))
                {
                    return directory.FullName;
                }
            }
        }

        throw new DirectoryNotFoundException("Could not find the SyncFactors repository root.");
    }
}

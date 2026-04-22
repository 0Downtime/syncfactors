using Microsoft.Extensions.Options;

namespace SyncFactors.MockSuccessFactors;

public sealed class MockSuccessFactorsOptions
{
    public string FixturePath { get; set; } = string.Empty;

    public string ServiceRoot { get; set; } = "http://127.0.0.1:18080/odata/v2";

    public MockAuthenticationOptions Authentication { get; set; } = new();

    public MockSyntheticPopulationOptions SyntheticPopulation { get; set; } = new();

    public MockEmpJobOptions EmpJob { get; set; } = new();

    public MockRuntimeOptions Runtime { get; set; } = new();

    public MockAdminOptions Admin { get; set; } = new();
}

public sealed class MockSyntheticPopulationOptions
{
    public bool Enabled { get; set; }

    public int TargetWorkerCount { get; set; } = 5000;

    public string? SeedFixturePath { get; set; }
}

public sealed class MockAuthenticationOptions
{
    public string ClientId { get; set; } = "mock-client-id";

    public string ClientSecret { get; set; } = "mock-client-secret";

    public string CompanyId { get; set; } = "MOCK";

    public string BasicUsername { get; set; } = "mock-user";

    public string BasicPassword { get; set; } = "mock-password";

    public string BearerToken { get; set; } = "mock-access-token";
}

public sealed class MockEmpJobOptions
{
    public bool IncludeTaggedPrehiresInDefaultListing { get; set; } = true;
}

public sealed class MockRuntimeOptions
{
    public string FixturePath { get; set; } = string.Empty;
}

public sealed class MockAdminOptions
{
    public bool Enabled { get; set; } = true;

    public bool RequireLoopback { get; set; } = true;

    public string Path { get; set; } = "/admin";
}

public sealed class MockSuccessFactorsOptionsSetup : IConfigureOptions<MockSuccessFactorsOptions>
{
    public void Configure(MockSuccessFactorsOptions options)
    {
        if (string.IsNullOrWhiteSpace(options.FixturePath))
        {
            var outputContentPath = Path.GetFullPath(Path.Combine(
                AppContext.BaseDirectory,
                "config",
                "mock-successfactors",
                "baseline-fixtures.json"));
            if (File.Exists(outputContentPath))
            {
                options.FixturePath = outputContentPath;
            }
            else
            {
                options.FixturePath = ResolveRepoRelativePath("config", "mock-successfactors", "baseline-fixtures.json");
            }
        }

        if (string.IsNullOrWhiteSpace(options.Runtime.FixturePath))
        {
            options.Runtime.FixturePath = ResolveRepoRelativePath("state", "runtime", "mock-successfactors.runtime-fixtures.json");
        }

        if (string.IsNullOrWhiteSpace(options.Admin.Path))
        {
            options.Admin.Path = "/admin";
        }
    }

    private static string ResolveRepoRelativePath(params string[] segments)
    {
        var paths = new List<string>
        {
            AppContext.BaseDirectory,
            "..",
            "..",
            "..",
            "..",
            ".."
        };
        paths.AddRange(segments);
        return Path.GetFullPath(Path.Combine(paths.ToArray()));
    }
}

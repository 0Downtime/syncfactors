using Microsoft.Extensions.DependencyInjection;
using SyncFactors.Domain;
using SyncFactors.Infrastructure;

namespace SyncFactors.Infrastructure.Tests;

public sealed class DirectoryServiceRuntimeServiceCollectionExtensionsTests
{
    [Fact]
    public void AddDirectoryServiceRuntimeGateways_ResolvesScaffoldGateways_ForMockQueuedRun()
    {
        using var fixture = new ScaffoldDataFixture();
        var services = new ServiceCollection();
        services.AddSingleton(new ScaffoldDataPathResolver(fixture.Path));
        services.AddSingleton<ScaffoldDataStore>();
        services.AddDirectoryServiceRuntimeGateways("mock");

        using var provider = services.BuildServiceProvider();

        var directoryGateway = provider.GetRequiredService<IDirectoryGateway>();
        var commandGateway = provider.GetRequiredService<IDirectoryCommandGateway>();

        Assert.IsType<ScaffoldDirectoryGateway>(directoryGateway);
        Assert.IsType<ScaffoldDirectoryCommandGateway>(commandGateway);
        Assert.IsNotType<ActiveDirectoryGateway>(directoryGateway);
        Assert.IsNotType<ActiveDirectoryCommandGateway>(commandGateway);
        Assert.DoesNotContain(services, descriptor => descriptor.ServiceType == typeof(ActiveDirectoryGateway));
        Assert.DoesNotContain(services, descriptor => descriptor.ServiceType == typeof(ActiveDirectoryCommandGateway));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("staging")]
    public void AddDirectoryServiceRuntimeGateways_RejectsUnknownProfiles_BeforeResolvingActiveDirectoryGateways(string? profile)
    {
        using var fixture = new ScaffoldDataFixture();
        var services = new ServiceCollection();
        services.AddSingleton(new ScaffoldDataPathResolver(fixture.Path));
        services.AddSingleton<ScaffoldDataStore>();
        var exception = Assert.Throws<InvalidOperationException>(() => services.AddDirectoryServiceRuntimeGateways(profile));

        Assert.Contains("SYNCFACTORS_RUN_PROFILE", exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(services, descriptor => descriptor.ServiceType == typeof(ActiveDirectoryGateway));
        Assert.DoesNotContain(services, descriptor => descriptor.ServiceType == typeof(ActiveDirectoryCommandGateway));
    }

    private sealed class ScaffoldDataFixture : IDisposable
    {
        private readonly string _directory = Directory.CreateTempSubdirectory("syncfactors-directory-runtime").FullName;

        public string Path => System.IO.Path.Combine(_directory, "scaffold-data.json");

        public void Dispose()
        {
            Directory.Delete(_directory, recursive: true);
        }
    }
}

using Microsoft.Extensions.DependencyInjection;
using SyncFactors.Contracts;
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

    [Fact]
    public void AddDirectoryServiceRuntimeGateways_ExposesOnlyDecoratedCommandGateway_WhenDecoratorIsProvided()
    {
        using var fixture = new ScaffoldDataFixture();
        var services = new ServiceCollection();
        services.AddSingleton(new ScaffoldDataPathResolver(fixture.Path));
        services.AddSingleton<ScaffoldDataStore>();
        services.AddDirectoryServiceRuntimeGateways("mock", (serviceProvider, inner) => new DecoratingDirectoryCommandGateway(inner));

        Assert.Single(services, descriptor => descriptor.ServiceType == typeof(IDirectoryCommandGateway));

        using var provider = services.BuildServiceProvider();
        var commandGateway = provider.GetRequiredService<IDirectoryCommandGateway>();
        var commandGateways = provider.GetServices<IDirectoryCommandGateway>();

        Assert.IsType<DecoratingDirectoryCommandGateway>(commandGateway);
        Assert.All(commandGateways, gateway => Assert.IsType<DecoratingDirectoryCommandGateway>(gateway));
    }

    [Fact]
    public void AddDirectoryServiceRuntimeGateways_ExposesOnlyDecoratedActiveDirectoryCommandGateway_ForRealProfile()
    {
        var services = new ServiceCollection();
        services.AddSingleton(new SyncFactorsConfigurationLoader(new SyncFactorsConfigPathResolver(null, null)));
        services.AddSingleton<IActiveDirectoryConnectionPool>(new ActiveDirectoryConnectionPool());
        services.AddLogging();
        services.AddDirectoryServiceRuntimeGateways("real", (serviceProvider, inner) => new DecoratingDirectoryCommandGateway(inner));

        Assert.Single(services, descriptor => descriptor.ServiceType == typeof(IDirectoryCommandGateway));

        using var provider = services.BuildServiceProvider();
        var commandGateway = provider.GetRequiredService<IDirectoryCommandGateway>();
        var commandGateways = provider.GetServices<IDirectoryCommandGateway>();

        var decoratedCommandGateway = Assert.IsType<DecoratingDirectoryCommandGateway>(commandGateway);
        Assert.IsType<ActiveDirectoryCommandGateway>(decoratedCommandGateway.Inner);
        Assert.All(commandGateways, gateway =>
        {
            var decoratedGateway = Assert.IsType<DecoratingDirectoryCommandGateway>(gateway);
            Assert.IsType<ActiveDirectoryCommandGateway>(decoratedGateway.Inner);
        });
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

    private sealed class DecoratingDirectoryCommandGateway(IDirectoryCommandGateway inner) : IDirectoryCommandGateway
    {
        public IDirectoryCommandGateway Inner => inner;

        public Task<DirectoryCommandResult> ExecuteAsync(DirectoryMutationCommand command, CancellationToken cancellationToken) =>
            inner.ExecuteAsync(command, cancellationToken);
    }
}

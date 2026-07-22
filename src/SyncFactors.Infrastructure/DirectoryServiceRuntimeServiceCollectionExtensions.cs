using Microsoft.Extensions.DependencyInjection;
using SyncFactors.Domain;

namespace SyncFactors.Infrastructure;

public static class DirectoryServiceRuntimeServiceCollectionExtensions
{
    public static IServiceCollection AddDirectoryServiceRuntimeGateways(
        this IServiceCollection services,
        string? runProfile,
        Func<IServiceProvider, IDirectoryCommandGateway, IDirectoryCommandGateway>? commandGatewayDecorator = null)
    {
        var useScaffoldDirectoryServices = DirectoryServiceRuntimeSelector.UseScaffoldDirectoryServices(runProfile);
        if (useScaffoldDirectoryServices)
        {
            services.AddSingleton<ScaffoldDirectoryGateway>();
            services.AddSingleton<ScaffoldDirectoryCommandGateway>();
            services.AddTransient<IDirectoryGateway>(serviceProvider => serviceProvider.GetRequiredService<ScaffoldDirectoryGateway>());
        }
        else
        {
            services.AddTransient<ActiveDirectoryGateway>();
            services.AddTransient<ActiveDirectoryCommandGateway>();
            services.AddTransient<IDirectoryGateway>(serviceProvider => serviceProvider.GetRequiredService<ActiveDirectoryGateway>());
        }

        services.AddTransient<IDirectoryCommandGateway>(serviceProvider =>
        {
            IDirectoryCommandGateway commandGateway = useScaffoldDirectoryServices
                ? serviceProvider.GetRequiredService<ScaffoldDirectoryCommandGateway>()
                : serviceProvider.GetRequiredService<ActiveDirectoryCommandGateway>();
            return commandGatewayDecorator?.Invoke(serviceProvider, commandGateway) ?? commandGateway;
        });

        return services;
    }
}

public static class DirectoryServiceRuntimeSelector
{
    public static bool UseScaffoldDirectoryServices(string? runProfile)
    {
        if (string.Equals(runProfile, "mock", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (string.Equals(runProfile, "real", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        throw new InvalidOperationException(
            "SYNCFACTORS_RUN_PROFILE must be explicitly set to either 'mock' or 'real' before directory services can be resolved.");
    }
}
using Microsoft.Extensions.DependencyInjection;
using SyncFactors.Domain;
using SyncFactors.Infrastructure;

namespace SyncFactors.Api;

internal static class DirectoryServiceRegistrationExtensions
{
    public static IServiceCollection AddDirectoryRuntimeServices(this IServiceCollection services)
    {
        services.AddSingleton<ScaffoldDirectoryGateway>();
        services.AddSingleton<ScaffoldDirectoryCommandGateway>();
        services.AddTransient<ActiveDirectoryGateway>();
        services.AddTransient<ActiveDirectoryCommandGateway>();
        services.AddTransient<IDirectoryGateway>(serviceProvider =>
        {
            var config = serviceProvider.GetRequiredService<SyncFactorsConfigurationLoader>().GetSyncConfig();
            var runProfile = Environment.GetEnvironmentVariable("SYNCFACTORS_RUN_PROFILE");
            return DirectoryServiceRuntimeSelector.UseScaffoldDirectoryServices(config, runProfile)
                ? serviceProvider.GetRequiredService<ScaffoldDirectoryGateway>()
                : serviceProvider.GetRequiredService<ActiveDirectoryGateway>();
        });
        services.AddTransient<IDirectoryCommandGateway>(serviceProvider =>
        {
            var config = serviceProvider.GetRequiredService<SyncFactorsConfigurationLoader>().GetSyncConfig();
            var runProfile = Environment.GetEnvironmentVariable("SYNCFACTORS_RUN_PROFILE");
            return DirectoryServiceRuntimeSelector.UseScaffoldDirectoryServices(config, runProfile)
                ? serviceProvider.GetRequiredService<ScaffoldDirectoryCommandGateway>()
                : serviceProvider.GetRequiredService<ActiveDirectoryCommandGateway>();
        });

        return services;
    }
}

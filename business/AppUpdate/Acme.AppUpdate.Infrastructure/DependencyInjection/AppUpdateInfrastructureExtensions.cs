using Microsoft.Extensions.DependencyInjection;

namespace Acme.AppUpdate.Infrastructure.DependencyInjection;

public static class AppUpdateInfrastructureExtensions
{
    public static IServiceCollection AddAppUpdateInfrastructure(this IServiceCollection services)
    {
        return services;
    }
}
using Acme.AppUpdate.Domain;
using Acme.AppUpdate.Infrastructure.DependencyInjection;
using Microsoft.Extensions.DependencyInjection;

namespace Acme.AppUpdate.Application.DependencyInjection;

public static class AppUpdateApplicationExtensions
{
    public static IServiceCollection AddAppUpdateApplication(this IServiceCollection services)
    {
        services.AddAppUpdateInfrastructure();
        services.AddSingleton<IUpdateService, UpdateService>();
        return services;
    }
}
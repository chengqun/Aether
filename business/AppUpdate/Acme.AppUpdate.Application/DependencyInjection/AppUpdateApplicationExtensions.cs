using Acme.AppUpdate.Application;
using Acme.AppUpdate.Domain;
using Microsoft.Extensions.DependencyInjection;

namespace Acme.AppUpdate.Application.DependencyInjection;

public static class AppUpdateApplicationExtensions
{
    public static IServiceCollection AddAppUpdateApplication(this IServiceCollection services)
    {
        services.AddSingleton<IUpdateService, UpdateService>();
        return services;
    }
}
using Acme.ServiceAccount.Application;
using Acme.ServiceAccount.Domain;
using Microsoft.Extensions.DependencyInjection;

namespace Acme.ServiceAccount.Application.DependencyInjection;

public static class ServiceAccountApplicationExtensions
{
    public static IServiceCollection AddServiceAccountApplication(this IServiceCollection services)
    {
        services.AddSingleton<IServiceAccountService, ServiceAccountService>();
        services.AddSingleton<IServiceAccountCacheService, ServiceAccountCacheService>();
        return services;
    }
}
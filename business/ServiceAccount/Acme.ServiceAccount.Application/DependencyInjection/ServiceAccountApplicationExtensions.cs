using Acme.ServiceAccount.Domain;
using Acme.ServiceAccount.Infrastructure.DependencyInjection;
using Microsoft.Extensions.DependencyInjection;

namespace Acme.ServiceAccount.Application.DependencyInjection;

public static class ServiceAccountApplicationExtensions
{
    public static IServiceCollection AddServiceAccountApplication(this IServiceCollection services, string? sqliteDbPath = null)
    {
        services.AddServiceAccountInfrastructure(sqliteDbPath);
        services.AddSingleton<IServiceAccountService, ServiceAccountService>();
        services.AddSingleton<IServiceAccountCacheService, ServiceAccountCacheService>();
        return services;
    }
}
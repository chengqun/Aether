using Acme.Ctp.Application;
using Acme.Ctp.Infrastructure.DependencyInjection;
using Microsoft.Extensions.DependencyInjection;

namespace Acme.Ctp.Application.DependencyInjection;

public static class CtpApplicationExtensions
{
    public static IServiceCollection AddCtpApplication(this IServiceCollection services, string? sqliteDbPath = null)
    {
        services.AddCtpInfrastructure(sqliteDbPath);
        services.AddSingleton<ICtpTradingService, CtpTradingService>();
        return services;
    }
}
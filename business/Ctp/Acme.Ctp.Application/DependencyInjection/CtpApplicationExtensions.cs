using Acme.Ctp.Application;
using Acme.Ctp.Domain;
using Microsoft.Extensions.DependencyInjection;

namespace Acme.Ctp.Application.DependencyInjection;

public static class CtpApplicationExtensions
{
    public static IServiceCollection AddCtpApplication(this IServiceCollection services)
    {
        services.AddSingleton<ICtpTradingService, CtpTradingService>();
        return services;
    }
}
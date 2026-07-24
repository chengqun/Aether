using Acme.Ctp.Application;
using Microsoft.Extensions.DependencyInjection;

namespace Acme.Ctp.Application.DependencyInjection;

/// <summary>
/// DI entry point for the CTP Application layer. Registers the trading
/// application service (thin orchestration over <c>ICtpSession</c>). Call
/// alongside <c>AddCtpInfrastructure</c> (which registers <c>ICtpSession</c>).
/// </summary>
public static class CtpApplicationExtensions
{
    public static IServiceCollection AddCtpApplication(this IServiceCollection services)
    {
        services.AddSingleton<ICtpTradingService, CtpTradingService>();
        return services;
    }
}

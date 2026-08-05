using Acme.StockTrading.Application;
using Microsoft.Extensions.DependencyInjection;

namespace Acme.StockTrading.Application.DependencyInjection;

public static class StockTradingApplicationExtensions
{
    public static IServiceCollection AddStockTradingApplication(this IServiceCollection services)
    {
        services.AddSingleton<IStockTradingApplicationService, StockTradingApplicationService>();
        return services;
    }
}

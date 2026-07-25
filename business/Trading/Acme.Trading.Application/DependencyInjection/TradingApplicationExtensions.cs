using Acme.Trading.Application;
using Microsoft.Extensions.DependencyInjection;

namespace Acme.Trading.Application.DependencyInjection;

/// <summary>
/// DI entry point for the Trading Application layer. Registers the backtest + live trading
/// orchestration services (thin wrappers over the Runtime abstraction ports). Runtime.Lean
/// (the engine impl) is registered separately via <c>AddTradingRuntimeLean</c> at the composition root.
/// </summary>
public static class TradingApplicationExtensions
{
    public static IServiceCollection AddTradingApplication(this IServiceCollection services)
    {
        services.AddSingleton<IBacktestApplicationService, BacktestApplicationService>();
        services.AddSingleton<ILiveTradingApplicationService, LiveTradingApplicationService>();
        return services;
    }
}

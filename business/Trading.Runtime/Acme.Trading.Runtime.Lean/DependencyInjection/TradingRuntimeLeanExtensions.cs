using Acme.Quotes.Domain;
using Acme.Trading.Domain;
using Acme.Trading.Domain.Strategies;
using Acme.Trading.Runtime.Domain;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Acme.Trading.Runtime.Lean.DependencyInjection;

/// <summary>
/// DI entry point for the Lean runtime adapter (the engine impl). Registers the
/// backtest + live trading runtime ports backed by the in-process QuantConnect LEAN engine.
/// Lean source (externals/Lean) is referenced only by this project; QuantConnect.* types stay here.
/// </summary>
/// <remarks>
/// <b>Consumes</b> (does NOT register): <c>ICtpSession</c> from <c>AddCtpInfrastructure</c>,
/// <c>IAnalysisManager</c> from <c>AddQuotesApplication</c>. Call this after those.
/// </remarks>
public static class TradingRuntimeLeanExtensions
{
    /// <param name="defaultDataFolder">Optional override for the LEAN data-folder (else BacktestEnvironment locates externals/Lean/Data).</param>
    public static IServiceCollection AddTradingRuntimeLean(
        this IServiceCollection services,
        string? defaultDataFolder = null)
    {
        // 内置策略注册（StrategyRegistry 是静态表，Lean 在 DI 外创建 algorithm，host 据此解析）
        StrategyRegistry.Register("SmaCross", () => new SmaCrossStrategy());
        StrategyRegistry.Register("Test", () => new TestStrategy());

        services.AddSingleton<IBacktestRuntime>(sp => new LeanBacktestRunner(
            analysisManager: sp.GetService<IAnalysisManager>(),
            logger: sp.GetService<ILogger<LeanBacktestRunner>>(),
            defaultDataFolder: defaultDataFolder));

        services.AddSingleton<ILiveTradingRuntime, LeanLiveTradingService>();
        return services;
    }
}

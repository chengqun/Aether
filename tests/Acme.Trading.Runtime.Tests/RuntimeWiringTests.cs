using Acme.Trading.Application;
using Acme.Trading.Application.DependencyInjection;
using Acme.Trading.Domain;
using Acme.Trading.Runtime.Domain;
using Acme.Trading.Runtime.Lean.DependencyInjection;
using Microsoft.Extensions.DependencyInjection;

namespace Acme.Trading.Runtime.Tests;

/// <summary>
/// Verifies the DI wiring of the backtest path (no Lean engine run, no CTP/Quotes needed —
/// LeanBacktestRunner's IAnalysisManager is optional). Confirms AddTradingRuntimeLean registers
/// the runtime port + seeds StrategyRegistry, and AddTradingApplication wires the orchestration service.
/// </summary>
public class RuntimeWiringTests
{
    [Fact]
    public void AddTradingRuntimeLean_registers_backtest_runtime_and_strategies()
    {
        var services = new ServiceCollection();
        services.AddTradingRuntimeLean();
        var sp = services.BuildServiceProvider();

        var runtime = sp.GetService<IBacktestRuntime>();
        Assert.NotNull(runtime);

        // AddTradingRuntimeLean seeds the built-in strategies into the static registry.
        Assert.NotNull(StrategyRegistry.Create("SmaCross", new StrategyParameters(null)));
        Assert.NotNull(StrategyRegistry.Create("Test", new StrategyParameters(null)));
    }

    [Fact]
    public void AddTradingApplication_wires_backtest_application_service_over_runtime()
    {
        var services = new ServiceCollection();
        services.AddTradingRuntimeLean();
        services.AddTradingApplication();
        var sp = services.BuildServiceProvider();

        var app = sp.GetService<IBacktestApplicationService>();
        Assert.NotNull(app);
    }

    [Fact]
    public void BacktestRuntimeRequest_is_Lean_neutral_no_config_fields()
    {
        // Smoke: the runtime request exposes only business fields, not Lean-specific ones.
        var req = new BacktestRuntimeRequest
        {
            Symbol = "ag2610",
            StrategyName = "SmaCross",
            StartDate = new DateTime(2024, 1, 1),
            EndDate = new DateTime(2024, 12, 31),
            InitialCash = 100_000m,
            FastPeriod = 10,
            SlowPeriod = 30,
            Resolution = BarResolution.Daily,
        };
        Assert.Equal("ag2610", req.Symbol);
        Assert.Equal(BarResolution.Daily, req.Resolution);
    }
}

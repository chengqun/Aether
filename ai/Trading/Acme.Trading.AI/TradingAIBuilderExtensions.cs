using System.Reflection;
using Acme.Trading.AI.Tools;
using Aether.AI.Hosting;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;

namespace Acme.Trading.AI;

/// <summary>
/// DI entry point for the Trading AI adapter. Registers the backtest tool only — no agent.
/// Agents are built dynamically by the host/page from the tool pool.
/// </summary>
public static class TradingAIBuilderExtensions
{
    /// <summary>The Trading tool names. MEAI AIFunctionFactory.Create keeps the method name as-is (RunBacktest).</summary>
    public static readonly string[] ToolNames = { "RunBacktest" };

    /// <summary>Registers the backtest tool only (no agent). Business layer
    /// (<c>AddTradingApplication</c>) is wired by the host.</summary>
    public static AetherAIBuilder AddTradingTools(this AetherAIBuilder builder)
    {
        var services = builder.Services;
        services.AddTransient<BacktestTool>();

        const string cat = "量化回测";
        builder.ConfigureToolRegistry(reg =>
        {
            reg.Register("RunBacktest", "运行策略历史回测", cat,
                sp => CreateTool<BacktestTool>(sp, nameof(BacktestTool.RunBacktest)));
        });
        return builder;
    }

    private static AIFunction CreateTool<T>(IServiceProvider sp, string methodName) where T : class
    {
        var tool = sp.GetRequiredService<T>();
        var method = typeof(T).GetMethod(methodName, BindingFlags.Public | BindingFlags.Instance)
            ?? throw new InvalidOperationException($"Tool method '{methodName}' not found on {typeof(T).Name}.");
        return AIFunctionFactory.Create(method, tool);
    }
}

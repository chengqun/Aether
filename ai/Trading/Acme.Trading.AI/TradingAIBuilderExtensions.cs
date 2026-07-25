using System.Reflection;
using Acme.Trading.AI.Tools;
using Aether.AI.Hosting;
using Aether.AI.Harness.Agents;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;

namespace Acme.Trading.AI;

/// <summary>
/// DI entry point for the Trading AI adapter. <see cref="AddTradingTools"/> registers the backtest tool
/// (no agent); <see cref="AddTrading"/> adds the trading-backtest agent on top.
/// Mirrors the Ctp/Quotes AI adapter pattern (AIFunctionFactory.Create).
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
        services.AddSingleton<AIFunction>(sp => CreateTool<BacktestTool>(sp, nameof(BacktestTool.RunBacktest)));

        builder.ConfigureToolRegistry(registry =>
        {
            const string cat = "量化回测";
            foreach (var name in ToolNames) registry.SetCategory(name, cat);
        });
        return builder;
    }

    /// <summary>Registers the Trading tools + the trading-backtest agent.</summary>
    public static AetherAIBuilder AddTrading(this AetherAIBuilder builder, string modelId)
    {
        AddTradingTools(builder);
        builder.AddAgent("trading-backtest", a => a
            .Name("Trading Backtest")
            .Instructions(
                "你是量化回测助手。可用工具：RunBacktest（用 SMA 双线交叉等策略跑一次历史回测，" +
                "返回收益/风险统计：Sharpe/最大回撤/净收益/胜率/交易次数）。按需调用工具，简洁回复。")
            .ModelId(modelId)
            .WithTools(ToolNames));
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

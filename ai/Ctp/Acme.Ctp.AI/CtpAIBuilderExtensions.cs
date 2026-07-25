using System.Reflection;
using Acme.Ctp.AI.Tools;
using Aether.AI.Hosting;
using Aether.AI.Harness.Agents;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;

namespace Acme.Ctp.AI;

public static class CtpAIBuilderExtensions
{
    public static readonly string[] ToolNames = {
        "GetStatus", "QueryAccount", "QueryPositions",
        "OrderInsert", "OrderAction",
        "QueryLatestTick", "QueryAllLatestTicks"
    };

    /// <summary>Registers the CTP tools only. Business layer (<c>AddCtpApplication</c>) is wired by the host.</summary>
    public static AetherAIBuilder AddCtpTools(this AetherAIBuilder builder)
    {
        var services = builder.Services;
        services.AddTransient<CtpLoginTool>();
        services.AddTransient<QueryAccountTool>();
        services.AddTransient<QueryPositionTool>();
        services.AddTransient<OrderInsertTool>();
        services.AddTransient<OrderActionTool>();
        services.AddTransient<QueryMarketDataTool>();

        services.AddSingleton<AIFunction>(sp => CreateTool<CtpLoginTool>(sp, nameof(CtpLoginTool.GetStatus)));
        services.AddSingleton<AIFunction>(sp => CreateTool<QueryAccountTool>(sp, nameof(QueryAccountTool.QueryAccountAsync)));
        services.AddSingleton<AIFunction>(sp => CreateTool<QueryPositionTool>(sp, nameof(QueryPositionTool.QueryPositionsAsync)));
        services.AddSingleton<AIFunction>(sp => CreateTool<OrderInsertTool>(sp, nameof(OrderInsertTool.OrderInsertAsync)));
        services.AddSingleton<AIFunction>(sp => CreateTool<OrderActionTool>(sp, nameof(OrderActionTool.OrderActionAsync)));
        services.AddSingleton<AIFunction>(sp => CreateTool<QueryMarketDataTool>(sp, nameof(QueryMarketDataTool.QueryLatestTickAsync)));
        services.AddSingleton<AIFunction>(sp => CreateTool<QueryMarketDataTool>(sp, nameof(QueryMarketDataTool.QueryAllLatestTicksAsync)));
        return builder;
    }

    public static AetherAIBuilder AddCtp(this AetherAIBuilder builder, string modelId)
    {
        AddCtpTools(builder);
        builder.AddAgent("ctp-trader", a => a
            .Name("CTP Trader")
            .Instructions(
                "你是期货交易助手，通过 CTP 工具操作期货账户。" +
                "可查询连接状态/资金账户/持仓/最新行情，可下单/撤单。" +
                "下单与撤单默认仅预览（requireConfirmation=true），需用户确认后才设 requireConfirmation=false 执行。")
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
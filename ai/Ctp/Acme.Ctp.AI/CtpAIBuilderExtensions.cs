using System.Reflection;
using Acme.Ctp.AI.Tools;
using Aether.AI.Hosting;
using Aether.AI.Harness.Tools;
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

        const string cat = "期货交易";
        builder.ConfigureToolRegistry(reg =>
        {
            reg.Register("GetStatus", "查询 CTP 连接状态", cat,
                sp => CreateTool<CtpLoginTool>(sp, nameof(CtpLoginTool.GetStatus)));
            reg.Register("QueryAccount", "查询资金账户", cat,
                sp => CreateTool<QueryAccountTool>(sp, nameof(QueryAccountTool.QueryAccountAsync)));
            reg.Register("QueryPositions", "查询持仓", cat,
                sp => CreateTool<QueryPositionTool>(sp, nameof(QueryPositionTool.QueryPositionsAsync)));
            reg.Register("OrderInsert", "报单（下单）", cat,
                sp => CreateTool<OrderInsertTool>(sp, nameof(OrderInsertTool.OrderInsertAsync)), ToolApprovalLevel.Dangerous);
            reg.Register("OrderAction", "撤单", cat,
                sp => CreateTool<OrderActionTool>(sp, nameof(OrderActionTool.OrderActionAsync)), ToolApprovalLevel.Dangerous);
            reg.Register("QueryLatestTick", "查询最新行情快照", cat,
                sp => CreateTool<QueryMarketDataTool>(sp, nameof(QueryMarketDataTool.QueryLatestTickAsync)));
            reg.Register("QueryAllLatestTicks", "查询所有订阅行情快照", cat,
                sp => CreateTool<QueryMarketDataTool>(sp, nameof(QueryMarketDataTool.QueryAllLatestTicksAsync)));
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

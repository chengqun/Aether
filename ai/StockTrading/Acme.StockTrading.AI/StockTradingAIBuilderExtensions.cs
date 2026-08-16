using System.Reflection;
using Acme.StockTrading.AI.Tools;
using Aether.AI.Hosting;
using Aether.AI.Core.Agents;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;

namespace Acme.StockTrading.AI;

public static class StockTradingAIBuilderExtensions
{
    public static readonly string[] ToolNames =
    {
        "StockTradingGetStatus",
        "StockTradingConnect",
        "StockTradingLaunchClient",
        "StockTradingQueryPositions",
        "StockTradingPlaceOrder",
        "StockTradingCancelOrder"
    };

    /// <summary>Registers the StockTrading (同花顺 GUI 自动化) tools only.
    /// Business layer (<c>AddStockTradingApplication</c> + <c>AddStockTradingFlaUi</c>) is wired by the host.</summary>
    public static AetherAIBuilder AddStockTradingTools(this AetherAIBuilder builder)
    {
        var services = builder.Services;
        services.AddTransient<StockTradingStatusTool>();
        services.AddTransient<StockTradingPositionTool>();
        services.AddTransient<StockTradingOrderTool>();

        const string cat = "股票交易";
        builder.ConfigureToolRegistry(reg =>
        {
            // 注意：CreateTool 第 3 参（显式工具名）必须 = 注册名。2 参 AIFunctionFactory.Create 会把
            // AIFunction.Name 设为去 Async 后缀的方法名（GetStatusAsync→"GetStatus"），与带前缀的注册名
            // 不符 → MAF 按 Name 分发时 CodeRuleBrain.Call("StockTradingGetStatus") 失败，且与 Ctp 的
            // "GetStatus" 撞名（同 agent 两边都选时 LLM 看到两个 GetStatus）。故显式传注册名进 3 参 Create。
            reg.Register("StockTradingGetStatus", "查询股票交易（同花顺）连接状态", cat,
                sp => CreateTool<StockTradingStatusTool>(sp, nameof(StockTradingStatusTool.GetStatusAsync), "StockTradingGetStatus"));
            reg.Register("StockTradingConnect", "连接同花顺下单客户端（需先启动并手动登录）", cat,
                sp => CreateTool<StockTradingStatusTool>(sp, nameof(StockTradingStatusTool.ConnectAsync), "StockTradingConnect"));
            reg.Register("StockTradingLaunchClient", "启动同花顺下单客户端 exe（不等待登录）", cat,
                sp => CreateTool<StockTradingStatusTool>(sp, nameof(StockTradingStatusTool.LaunchClientAsync), "StockTradingLaunchClient"));
            reg.Register("StockTradingQueryPositions", "查询股票持仓", cat,
                sp => CreateTool<StockTradingPositionTool>(sp, nameof(StockTradingPositionTool.QueryPositionsAsync), "StockTradingQueryPositions"));
            // 下单/撤单不可逆 → Dangerous（否则默认 Safe 会在审批开启时自动执行）
            reg.Register("StockTradingPlaceOrder", "股票下单（限价买入/卖出）", cat,
                sp => CreateTool<StockTradingOrderTool>(sp, nameof(StockTradingOrderTool.PlaceOrderAsync), "StockTradingPlaceOrder"),
                ToolApprovalLevel.Dangerous);
            reg.Register("StockTradingCancelOrder", "股票撤单", cat,
                sp => CreateTool<StockTradingOrderTool>(sp, nameof(StockTradingOrderTool.CancelOrderAsync), "StockTradingCancelOrder"),
                ToolApprovalLevel.Dangerous);
        });
        return builder;
    }

    private static AIFunction CreateTool<T>(IServiceProvider sp, string methodName, string name) where T : class
    {
        var tool = sp.GetRequiredService<T>();
        var method = typeof(T).GetMethod(methodName, BindingFlags.Public | BindingFlags.Instance)
            ?? throw new InvalidOperationException($"Tool method '{methodName}' not found on {typeof(T).Name}.");
        // 3 参形式显式设 AIFunction.Name = 注册名（见上方注册处注释）。
        return AIFunctionFactory.Create(method, tool, name);
    }
}

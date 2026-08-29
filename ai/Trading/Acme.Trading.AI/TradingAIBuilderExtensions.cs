using System.Reflection;
using Acme.Trading.AI.Tools;
using Aether.AI.Hosting;
using Aether.AI.Core.Agents;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;

namespace Acme.Trading.AI;

/// <summary>
/// DI entry point for the Trading AI adapter. Registers the backtest + create-strategy tools — no agent.
/// Agents are built dynamically by the host/page from the tool pool.
/// </summary>
public static class TradingAIBuilderExtensions
{
    /// <summary>The Trading tool names. MEAI AIFunctionFactory.Create keeps the method name as-is.</summary>
    public static readonly string[] ToolNames = { "RunBacktest", "RunStrategy", "CreateStrategy", "CreateYamlStrategy", "CreateLeanAlgorithm" };

    /// <summary>Registers the backtest + signal-probe + create-strategy tools (no agent). Business layer
    /// (<c>AddTradingApplication</c>) is wired by the host.</summary>
    public static AetherAIBuilder AddTradingTools(this AetherAIBuilder builder)
    {
        var services = builder.Services;
        services.AddTransient<BacktestTool>();
        services.AddTransient<RunStrategyTool>();
        services.AddTransient<CreateStrategyTool>();
        services.AddTransient<CreateYamlStrategyTool>();
        services.AddTransient<CreateLeanAlgorithmTool>();

        const string cat = "量化回测";
        // 回测/策略工具依赖 Lean 引擎 + Trading.Runtime.Lean（externals/Lean）——仅桌面端可执行。
        const HostCapability host = HostCapability.DesktopOnly;
        builder.ConfigureToolRegistry(reg =>
        {
            reg.Register("RunBacktest", "运行策略历史回测", cat,
                sp => CreateTool<BacktestTool>(sp, nameof(BacktestTool.RunBacktest)), hostCapability: host);
            // 信号探针：进程内逐根求信号，不起 Lean、不算 PnL（只读，Safe）。镜像 run_rule。
            reg.Register("RunStrategy", "探针：取最近若干根 bar 进程内求策略信号（不起引擎，不算 PnL）", cat,
                sp => CreateTool<RunStrategyTool>(sp, nameof(RunStrategyTool.RunStrategy)), hostCapability: host);
            reg.Register("CreateStrategy", "创建交易策略（C# 源码笔记）", cat,
                sp => CreateTool<CreateStrategyTool>(sp, nameof(CreateStrategyTool.CreateStrategy)), ToolApprovalLevel.Confirm, host);
            // 声明式 YAML 策略（无 LLM 写代码的风险，schema 有界）：解析验证 + 落 Strategy 笔记。镜像 CreateStrategy。
            reg.Register("CreateYamlStrategy", "创建声明式 YAML 交易策略（配方笔记）", cat,
                sp => CreateTool<CreateYamlStrategyTool>(sp, nameof(CreateYamlStrategyTool.CreateYamlStrategy)), ToolApprovalLevel.Confirm, host);
            // Lean 算法策略（继承 QCAlgorithm，复用 Lean 全部能力）：经 ILeanAlgorithmScriptingService.TryCompile 即时编译验证 + 落 LeanAlgorithm 笔记。镜像 CreateStrategy。
            reg.Register("CreateLeanAlgorithm", "创建 Lean 算法策略（C# 源码笔记，继承 QCAlgorithm，复用 Lean 全部能力）", cat,
                sp => CreateTool<CreateLeanAlgorithmTool>(sp, nameof(CreateLeanAlgorithmTool.CreateLeanAlgorithm)), ToolApprovalLevel.Confirm, host);
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

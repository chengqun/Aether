using System.Reflection;
using Acme.Automation.AI.Tools;
using Aether.AI.Hosting;
using Aether.AI.Harness.Agents;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;

namespace Acme.Automation.AI;

/// <summary>
/// DI entry point for the Automation AI adapter. Same shape as the other <c>ai/&lt;Context&gt;</c>
/// adapters: <see cref="AddAutomationTools"/> registers the tools (no agent);
/// <see cref="AddAutomation"/> adds the automation agent on top.
/// </summary>
public static class AutomationAIBuilderExtensions
{
    /// <summary>The Automation tool names. MEAI AIFunctionFactory.Create strips the "Async" suffix.</summary>
    public static readonly string[] ToolNames = {
        "ListAutomationTasks", "CreateAutomationTask", "DeleteAutomationTask",
        "StartScheduler", "StopScheduler"
    };

    /// <summary>Registers the Automation tools only (no agent). Business layer
    /// (<c>AddAutomationApplication</c>) is wired by the host.</summary>
    public static AetherAIBuilder AddAutomationTools(this AetherAIBuilder builder)
    {
        var services = builder.Services;
        services.AddTransient<AutomationTool>();

        const string cat = "自动化";
        builder.ConfigureToolRegistry(reg =>
        {
            reg.Register("ListAutomationTasks", "列出自动化任务", cat,
                sp => CreateTool<AutomationTool>(sp, nameof(AutomationTool.ListAutomationTasksAsync)));
            reg.Register("CreateAutomationTask", "新建自动化任务", cat,
                sp => CreateTool<AutomationTool>(sp, nameof(AutomationTool.CreateAutomationTaskAsync)));
            reg.Register("DeleteAutomationTask", "删除自动化任务", cat,
                sp => CreateTool<AutomationTool>(sp, nameof(AutomationTool.DeleteAutomationTaskAsync)));
            reg.Register("StartScheduler", "启动调度器", cat,
                sp => CreateTool<AutomationTool>(sp, nameof(AutomationTool.StartSchedulerAsync)));
            reg.Register("StopScheduler", "停止调度器", cat,
                sp => CreateTool<AutomationTool>(sp, nameof(AutomationTool.StopSchedulerAsync)));
        });
        return builder;
    }

    /// <summary>Registers the Automation tools + the automation agent.</summary>
    public static AetherAIBuilder AddAutomation(this AetherAIBuilder builder, string modelId)
    {
        AddAutomationTools(builder);
        builder.AddAgent("automation-scheduler", a => a
            .Name("Automation Scheduler")
            .Instructions(
                "你是自动化任务调度助手。可用工具：列出/新建/删除自动化任务、启停调度器。" +
                "任务按计划（Daily/Interval/Weekly）触发指定 agent。按需调用工具，简洁回复。")
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

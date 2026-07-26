using System.Reflection;
using Acme.Automation.AI.Tools;
using Aether.AI.Hosting;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;

namespace Acme.Automation.AI;

/// <summary>
/// DI entry point for the Automation AI adapter. Registers the tools only — no agent.
/// Agents are built dynamically by the host/page from the tool pool.
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

    private static AIFunction CreateTool<T>(IServiceProvider sp, string methodName) where T : class
    {
        var tool = sp.GetRequiredService<T>();
        var method = typeof(T).GetMethod(methodName, BindingFlags.Public | BindingFlags.Instance)
            ?? throw new InvalidOperationException($"Tool method '{methodName}' not found on {typeof(T).Name}.");
        return AIFunctionFactory.Create(method, tool);
    }
}

using Aether.AI.Hosting;
using Microsoft.Extensions.DependencyInjection;

namespace Acme.Agents.AI;

/// <summary>
/// DI entry point for the custom-Agent bridge adapter. Registers the catalog
/// (Notes-backed CRUD + tool pool) and the runner (AgentDefinition → HarnessRuntime).
/// No tools of its own — agents are built dynamically by the host/page from the
/// shared tool pool contributed by the other adapters' AddXxxTools.
/// </summary>
public static class AgentsAIBuilderExtensions
{
    /// <summary>注册自定义 Agent 目录 + 运行器（依赖已注册的 INoteService 与 ToolRegistry）。</summary>
    public static AetherAIBuilder AddCustomAgents(this AetherAIBuilder builder)
    {
        var services = builder.Services;
        // 模型源默认仅"默认"；宿主可注册自己的 IModelSource 覆盖（如桌面端的 LLM 配置源）。
        services.AddSingleton<IModelSource, DefaultModelSource>();
        services.AddSingleton<ICustomAgentCatalog, CustomAgentCatalog>();
        services.AddSingleton<ICustomAgentRunner, CustomAgentRunner>();
        services.AddSingleton<IAgentMarketplaceService, AgentMarketplaceService>();
        return builder;
    }

    /// <summary>注册自定义 Workflow 目录 + 运行器（依赖 AddCustomAgents 已注册的 catalog/HarnessRuntime/AgentFactory）。</summary>
    public static AetherAIBuilder AddCustomWorkflows(this AetherAIBuilder builder)
    {
        var services = builder.Services;
        services.AddSingleton<ICustomWorkflowCatalog, CustomWorkflowCatalog>();
        services.AddSingleton<ICustomWorkflowRunner, CustomWorkflowRunner>();
        return builder;
    }
}

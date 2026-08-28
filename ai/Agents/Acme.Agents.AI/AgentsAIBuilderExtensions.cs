using Aether.AI.Core.Agents;
using Aether.AI.Hosting;
using Acme.Notes.Domain.Agents;
using Microsoft.Extensions.DependencyInjection;

namespace Acme.Agents.AI;

/// <summary>
/// DI entry point for the custom-Agent bridge adapter. Registers the runner
/// (AgentDefinition → HarnessRuntime) + model source.
/// Catalog (DirectoryAgentCatalog/DirectoryWorkflowCatalog) is registered by the host
/// composition root, not here — the host binds the labs root via Func&lt;string?&gt;.
/// No tools of its own — agents are built dynamically by the host/page from the
/// shared tool pool contributed by the other adapters' AddXxxTools.
/// </summary>
public static class AgentsAIBuilderExtensions
{
    /// <summary>注册自定义 Agent 运行器 + 模型源（catalog 由宿主注册 DirectoryAgentCatalog）。</summary>
    public static AetherAIBuilder AddCustomAgents(this AetherAIBuilder builder)
    {
        var services = builder.Services;
        // 模型源默认仅"默认"；宿主可注册自己的 IModelSource 覆盖（如桌面端的 LLM 配置源）。
        services.AddSingleton<IModelSource, DefaultModelSource>();
        services.AddSingleton<ICustomAgentRunner, CustomAgentRunner>();
        return builder;
    }

    /// <summary>注册自定义 Workflow 运行器（catalog 由宿主注册 DirectoryWorkflowCatalog）。</summary>
    public static AetherAIBuilder AddCustomWorkflows(this AetherAIBuilder builder)
    {
        var services = builder.Services;
        services.AddSingleton<ICustomWorkflowRunner, CustomWorkflowRunner>();
        return builder;
    }
}

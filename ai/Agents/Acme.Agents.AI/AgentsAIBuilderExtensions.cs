using Aether.AI.Core.Agents;
using Aether.AI.Harness;
using Aether.AI.Hosting;
using Aether.AI.Harness.Agents;
using Acme.Notes.Domain;
using Acme.Notes.Domain.Agents;
using Microsoft.Agents.AI;
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
    /// <summary>注册自定义 Agent 运行器 + 模型源 + 后台 Agent 工厂（catalog 由宿主注册 DirectoryAgentCatalog）。</summary>
    public static AetherAIBuilder AddCustomAgents(this AetherAIBuilder builder)
    {
        var services = builder.Services;
        // 模型源默认仅"默认"；宿主可注册自己的 IModelSource 覆盖（如桌面端的 LLM 配置源）。
        services.AddSingleton<IModelSource, DefaultModelSource>();
        services.AddSingleton<ICustomAgentRunner, CustomAgentRunner>();

        // 后台 Agent（orchestrator 分派子任务）工厂：AgentFactory 构建 agent 时经此把 definition.BackgroundAgents
        // 里的 Name 白名单解析成子 AIAgent 列表。catalog 由宿主在 AddCustomAgents 之后绑定为 DirectoryAgentCatalog，
        // 故在委托内懒解析；ListAsync 读本地 YAML 且运行在 agent 构建路径（非 UI 线程），sync-over-async 安全。
        services.AddSingleton<Func<AgentDefinition, IReadOnlyList<AIAgent>?>>(sp =>
        {
            var catalog = sp.GetRequiredService<ICustomAgentCatalog>();
            var agentFactory = sp.GetRequiredService<AgentFactory>();
            Func<AgentDefinition, IReadOnlyList<AIAgent>?> resolveSubAgents = definition =>
            {
                if (definition.BackgroundAgents is not { Count: > 0 }) return null;
                // 目录里按 NodeType==Agent 过滤：仅普通 LLM agent 可作后台子 agent（排除规则/代码规则）。
                var all = catalog.ListAsync(default).GetAwaiter().GetResult();
                var byName = all
                    .Where(d => d.NodeType == NoteNodeType.Agent && !string.IsNullOrWhiteSpace(d.Name))
                    .ToDictionary(d => d.Name, StringComparer.OrdinalIgnoreCase);

                var result = new List<AIAgent>();
                foreach (var name in definition.BackgroundAgents)
                {
                    if (string.IsNullOrWhiteSpace(name)) continue;
                    if (!byName.TryGetValue(name, out var def)) continue; // 白名单名不存在/非 LLM agent → 静默跳过
                    var subDef = CustomAgentRunner.ToAgentDefinition(def.Id.ToString(), def);
                    result.Add(agentFactory.Create(subDef));
                }
                return result.Count > 0 ? (IReadOnlyList<AIAgent>)result : null;
            };
            return resolveSubAgents;
        });

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

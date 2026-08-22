using System.ComponentModel;
using Acme.Notes.Domain.Agents;
using Aether.AI.Core.Agents;

namespace Acme.Desktop.Shared.Shell;

/// <summary>
/// <c>agent_add_tools</c> / <c>agent_reload</c> 工具：让 agent 把「已装进 ToolRegistry 的工具勾选到当前 agent」并
/// 「重新召唤自己」——即页面上「专家编辑器勾选工具 + 重新召唤」两个操作的 AI 版。配合 <c>McpManagementTool.mcp_add_server</c>
/// 构成完整自举闭环：装 MCP → 勾选工具 → 重新召唤 → 下轮起用带完整 schema 的新工具。
/// </summary>
/// <remarks>
/// <list type="bullet">
///   <item>「勾选」= 更新 <see cref="CustomAgentDefinition.Tools"/>（走 <see cref="ICustomAgentCatalog.SaveAsync"/>，
///   与 ExpertEditViewModel 保存同一持久化路径）。加工具须在重新召唤之前，否则 <c>AgentFactory.ResolveAll(def.Tools)</c>
///   拿不到新名。</item>
///   <item>「重新加载」= <see cref="ICustomAgentRunner.RefreshAgent"/>，使下轮 run 重建 agent（带新 Tools 的
///   <see cref="AgentDefinition"/>），但保留 conversationId→sessionId 映射与会话记忆（多轮上下文不断）。
///   仅换 agent 实例，不清 session。</item>
///   <item>定位「当前 agent」经 ambient 上下文 <see cref="AgentExecutionContext.CurrentAgentId"/>（Harness 每轮 run 写入）。</item>
/// </list>
/// 与 <see cref="McpManagementTool"/> 同属宿主层（依赖 Notes.Domain + Core.Agents 契约，不引 ai/Agents impl）。
/// </remarks>
public sealed class AgentManagementTool
{
    private readonly ICustomAgentCatalog _catalog;
    private readonly ICustomAgentRunner _runner;

    public AgentManagementTool(ICustomAgentCatalog catalog, ICustomAgentRunner runner)
    {
        _catalog = catalog;
        _runner = runner;
    }

    public sealed class AddToolsOutput
    {
        public bool Success { get; set; }
        public string? AgentId { get; set; }
        public List<string> Tools { get; set; } = new();
        public string? Error { get; set; }
    }

    public sealed class ReloadOutput
    {
        public bool Success { get; set; }
        public string? AgentId { get; set; }
        public string? Error { get; set; }
    }

    [Description("勾选工具给当前正在运行的 agent（把工具名加入其 Tools 列表并持久化）。" +
                 "常用于 mcp_add_server 装好新 MCP server 后，把它的工具勾进当前 agent。" +
                 "勾选后需再调 agent_reload 重新召唤才生效（AgentFactory 只在构建时按 Tools 解析）。" +
                 "toolNames 传工具全名数组；可用 mcp_list_servers 查看新 server 的工具名。此操作改写 agent 配置，需要用户审批确认。")]
    public async Task<AddToolsOutput> AddToolsAsync(
        [Description("要勾选的工具全名（字符串数组）。未注册的名字会被静默跳过。")] string[] toolNames,
        CancellationToken cancellationToken = default)
    {
        var agentIdText = AgentExecutionContext.CurrentAgentId.Value;
        if (string.IsNullOrEmpty(agentIdText) || !Guid.TryParse(agentIdText, out var agentId))
            return new AddToolsOutput { Success = false, Error = "不在 agent 运行上下文内（无法定位当前 agent）。" };

        var def = await _catalog.GetAsync(agentId, cancellationToken).ConfigureAwait(false);
        if (def is null)
            return new AddToolsOutput { Success = false, AgentId = agentIdText, Error = "找不到当前 agent 定义。" };

        var existing = new HashSet<string>(def.Tools);
        foreach (var n in toolNames)
            if (!string.IsNullOrWhiteSpace(n)) existing.Add(n.Trim());

        def.Tools = existing.ToList();
        await _catalog.SaveAsync(def, cancellationToken).ConfigureAwait(false);
        return new AddToolsOutput { Success = true, AgentId = agentIdText, Tools = existing.ToList() };
    }

    [Description("重新加载当前 agent 的工具集：使其缓存失效，下一轮 run 重建（带上最新勾选的工具、含完整 schema）。" +
                 "通常在 agent_add_tools 之后调用。此操作保留当前会话的对话记忆（多轮上下文不断），只换 agent 实例——" +
                 "区别于「编辑配置后的硬重置」（清记忆）。此操作需要用户审批确认。")]
    public Task<ReloadOutput> ReloadAsync(CancellationToken cancellationToken = default)
    {
        var agentIdText = AgentExecutionContext.CurrentAgentId.Value;
        if (string.IsNullOrEmpty(agentIdText) || !Guid.TryParse(agentIdText, out var agentId))
            return Task.FromResult(new ReloadOutput { Success = false, Error = "不在 agent 运行上下文内（无法定位当前 agent）。" });

        _runner.RefreshAgent(agentId);
        return Task.FromResult(new ReloadOutput { Success = true, AgentId = agentIdText });
    }
}

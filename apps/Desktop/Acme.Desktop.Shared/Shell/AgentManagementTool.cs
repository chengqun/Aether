using System.ComponentModel;
using Aether.AI.Core.Agents;

namespace Acme.Desktop.Shared.Shell;

/// <summary>
/// <c>agent_reload</c> 工具：让 agent 手动重新加载自己的工具集（重建 agent，带最新勾选的工具）。
/// 通常是可选的——<see cref="McpManagementTool"/> 的 mcp_add_server 已自动勾选 + 重建；
/// 此工具供「已手动勾选工具后想让当前 agent 立即生效」或「重新加载」场景。保留会话记忆（多轮上下文不断）。
/// </summary>
public sealed class AgentManagementTool
{
    private readonly ICustomAgentRunner _runner;

    public AgentManagementTool(ICustomAgentRunner runner)
    {
        _runner = runner;
    }

    public sealed class ReloadOutput
    {
        public bool Success { get; set; }
        public string? AgentId { get; set; }
        public string? Error { get; set; }
    }

    [Description("重新加载当前 agent 的工具集：使其缓存失效，下一轮 run 重建（带上最新勾选的工具、含完整 schema）。" +
                 "通常在手动勾选工具后调用，让当前 agent 立即用上而不必重新召唤。保留当前会话的对话记忆（多轮上下文不断）。需要用户审批确认。")]
    public Task<ReloadOutput> ReloadAsync(CancellationToken cancellationToken = default)
    {
        var agentIdText = AgentExecutionContext.CurrentAgentId.Value;
        if (string.IsNullOrEmpty(agentIdText) || !Guid.TryParse(agentIdText, out var agentId))
            return Task.FromResult(new ReloadOutput { Success = false, Error = "不在 agent 运行上下文内（无法定位当前 agent）。" });

        _runner.RefreshAgent(agentId);
        return Task.FromResult(new ReloadOutput { Success = true, AgentId = agentIdText });
    }
}

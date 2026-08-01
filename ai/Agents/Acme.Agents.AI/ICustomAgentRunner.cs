using Acme.Notes.Domain.Agents;
using Aether.AI.Core.Agents;

namespace Acme.Agents.AI;

/// <summary>
/// 自定义 Agent 运行器：把目录中的 <see cref="CustomAgentDefinition"/> 映射为平台
/// <c>AgentDefinition</c> 并交由 <c>HarnessRuntime</c> 流式执行。UI 无关。
/// </summary>
public interface ICustomAgentRunner
{
    /// <summary>
    /// 运行一轮对话，经 <paramref name="handlers"/> 流式回调答案 token / 推理 / 工具事件 / 审批请求 /
    /// 待办 / 错误 / 用量。每个 conversationId 拥有独立的运行时会话（多会话间记忆隔离）；同一会话的
    /// 后续轮次复用其会话（多轮记忆）。首次运行会按需注册 agent。
    /// <paramref name="approvalResponse"/> 非空表示审批恢复轮（带决定二次调用以恢复被审批挂起的工具执行）。
    /// </summary>
    Task RunStreamingAsync(
        Guid conversationId,
        Guid agentId,
        string message,
        StreamHandlers? handlers = null,
        ToolApprovalDecision? approvalResponse = null,
        string? desiredMode = null,
        CancellationToken cancellationToken = default);

    /// <summary>读取当前会话的 AgentMode（plan/execute…）；未启用 AgentModeProvider 返回 null。</summary>
    Task<string?> GetModeAsync(Guid conversationId, CancellationToken cancellationToken = default);

    /// <summary>设置当前会话的 AgentMode；未启用返回 false。</summary>
    Task<bool> SetModeAsync(Guid conversationId, string mode, CancellationToken cancellationToken = default);

    /// <summary>
    /// 使该 agent 的缓存失效（编辑/删除后调用，清掉该 agent 所有会话，下次运行重建 agent 与会话）。
    /// </summary>
    void Invalidate(Guid agentId);
}

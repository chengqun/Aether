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
    /// 运行一轮对话，流式回调每个 token 与工具调用/结果事件。每个 conversationId 拥有独立的运行时会话
    /// （多会话间记忆隔离）；同一会话的后续轮次复用其会话（多轮记忆）。首次运行会按需注册 agent。
    /// <paramref name="onToolEvent"/> 用于在 UI 展示思考过程中的工具调用输入/输出。
    /// </summary>
    Task RunStreamingAsync(
        Guid conversationId,
        Guid agentId,
        string message,
        Func<string, CancellationToken, Task>? onToken = null,
        Func<ToolStreamEvent, CancellationToken, Task>? onToolEvent = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// 使该 agent 的缓存失效（编辑/删除后调用，清掉该 agent 所有会话，下次运行重建 agent 与会话）。
    /// </summary>
    void Invalidate(Guid agentId);
}

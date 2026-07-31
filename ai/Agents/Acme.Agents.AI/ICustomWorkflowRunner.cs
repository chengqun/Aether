using Acme.Notes.Domain.Agents;
using Aether.AI.Core.Agents;

namespace Acme.Agents.AI;

/// <summary>
/// 自定义 Workflow 运行器：把目录中的 <see cref="CustomWorkflowDefinition"/> 建成 MAF Handoff workflow
/// （参与者用 AgentFactory 建成 AIAgent），包成单个 AIAgent 后经 HarnessRuntime 流式运行。
/// 每个 conversationId 拥有独立会话（多轮记忆）；workflow 编辑后调 <see cref="Invalidate"/> 重建。
/// 与 <see cref="ICustomAgentRunner"/> 同构（同样的 onToken/onToolEvent 回调），聊天页可统一 dispatch。
/// </summary>
public interface ICustomWorkflowRunner
{
    Task RunStreamingAsync(
        Guid conversationId,
        Guid workflowId,
        string message,
        StreamHandlers? handlers = null,
        ToolApprovalDecision? approvalResponse = null,
        CancellationToken cancellationToken = default);

    /// <summary>使该 workflow 的缓存会话失效（编辑/删除后调用）。</summary>
    void Invalidate(Guid workflowId);
}

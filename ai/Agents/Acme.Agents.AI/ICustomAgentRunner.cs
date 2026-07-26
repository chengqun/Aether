namespace Acme.Agents.AI;

/// <summary>
/// 自定义 Agent 运行器：把目录中的 <see cref="CustomAgentDefinition"/> 映射为平台
/// <c>AgentDefinition</c> 并交由 <c>HarnessRuntime</c> 流式执行。UI 无关。
/// </summary>
public interface ICustomAgentRunner
{
    /// <summary>
    /// 运行一轮对话，流式回调每个 token。首次运行会按需注册 agent；
    /// 同一 agentId 的后续轮次复用会话（多轮记忆）。
    /// </summary>
    Task RunStreamingAsync(
        Guid agentId,
        string message,
        Func<string, CancellationToken, Task>? onToken = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// 使该 agent 的缓存失效（编辑/删除后调用，下次运行将用最新配置重建 agent 与会话）。
    /// </summary>
    void Invalidate(Guid agentId);
}

namespace Acme.Automation.Domain.Interfaces;

/// <summary>
/// 触发一次 agent 执行（业务端口，实现由宿主/AI 适配器提供，业务层不依赖 AI 平台）。
/// 复用聊天会话流程：以 <paramref name="conversationId"/> 标识的会话持久化到 IConversationStore，
/// 从而自动出现在「最近会话」并保留跨次触发的记忆（App 打开期间）。
/// </summary>
public interface IAgentExecutor
{
    /// <param name="agentId">目标 agent 的 Guid 字符串。</param>
    /// <param name="conversationId">该任务固定的会话 Id（跨次触发复用同一条会话）。</param>
    /// <param name="taskName">任务名（首次创建会话时用作标题）。</param>
    /// <param name="userMessage">触发时发给 agent 的消息。</param>
    /// <returns>agent 的完整回复文本（用于记录 LastExecutionResult）。</returns>
    Task<string> ExecuteAsync(
        string agentId,
        Guid conversationId,
        string taskName,
        string userMessage,
        CancellationToken cancellationToken = default);
}

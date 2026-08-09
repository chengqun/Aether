using System.Text;
using Acme.Agents.AI;
using Acme.Automation.Domain.Interfaces;
using Aether.AI.Core.Agents;
using Aether.AI.Core.Conversations;
using Aether.AI.Core.Messages;
using Aether.AI.Memory;

namespace Acme.Desktop.Mvvm;

/// <summary>
/// <see cref="IAgentExecutor"/> 的宿主实现：复用聊天页同一条会话流程触发专家。
/// 每个任务持有一个固定的 <c>ConversationId</c>——触发时 get-or-create 该会话，
/// 把 prompt 作为用户消息、agent 回复作为助手消息落库到 <see cref="IConversationStore"/>，
/// 于是定时运行自然出现在「最近会话」，并在 App 打开期间通过 <see cref="ICustomAgentRunner"/>
/// 的按会话缓存保留跨次记忆。业务层不依赖 AI 平台，故实现住宿主 VM 库。
/// </summary>
public sealed class ConversationBackedAgentExecutor : IAgentExecutor
{
    private readonly ICustomAgentRunner _runner;
    private readonly IConversationStore _conversationStore;
    private readonly ICustomAgentCatalog _catalog;

    public ConversationBackedAgentExecutor(
        ICustomAgentRunner runner,
        IConversationStore conversationStore,
        ICustomAgentCatalog catalog)
    {
        _runner = runner;
        _conversationStore = conversationStore;
        _catalog = catalog;
    }

    public async Task<string> ExecuteAsync(
        string agentId,
        Guid conversationId,
        string taskName,
        string userMessage,
        CancellationToken cancellationToken = default)
    {
        if (!Guid.TryParse(agentId, out var agentGuid))
            throw new ArgumentException($"无效的 agentId（非 Guid）：{agentId}", nameof(agentId));

        // 解析专家显示名（会话列表展示用）。
        var def = await _catalog.GetAsync(agentGuid, cancellationToken);
        var agentName = def is null
            ? agentId
            : (string.IsNullOrWhiteSpace(def.DisplayName) ? def.Name : def.DisplayName);

        // get-or-create 该任务的会话（与聊天页同一条会话）。
        var conv = await _conversationStore.GetAsync(conversationId);
        if (conv is null)
        {
            conv = new Conversation
            {
                ConversationId = conversationId,
                AgentId = agentId,
                AgentName = agentName,
                Title = string.IsNullOrWhiteSpace(taskName) ? "定时任务" : taskName,
            };
        }
        conv.Append(Message.User(userMessage));

        // 流式运行（与聊天页相同的运行器路径），累积完整回复。
        var buffer = new StringBuilder();
        try
        {
            await _runner.RunStreamingAsync(
                conversationId,
                agentGuid,
                userMessage,
                new StreamHandlers(OnToken: async (token, _) =>
                {
                    buffer.Append(token);
                    await Task.CompletedTask;
                }),
                cancellationToken: cancellationToken);
        }
        catch (Exception ex)
        {
            buffer.Append($"\n\n[运行失败：{ex.Message}]");
        }

        var reply = buffer.ToString();
        conv.Append(Message.Assistant(reply));
        await _conversationStore.SaveAsync(conv);

        return reply;
    }
}

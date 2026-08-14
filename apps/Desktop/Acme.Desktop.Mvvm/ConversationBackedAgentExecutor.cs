using System.Text;
using Acme.Agents.AI;
using Acme.Automation.Domain.Interfaces;
using Aether.AI.Core.Agents;
using Aether.AI.Core.Conversations;
using Aether.AI.Core.Conversations.Runs;
using Aether.AI.Memory;

namespace Acme.Desktop.Mvvm;

/// <summary>
/// <see cref="IAgentExecutor"/> 的宿主实现：复用聊天页同一条会话流程触发专家。
/// 每个任务持有一个固定的 <c>ConversationId</c>——触发时 get-or-create 该会话，
/// 把 prompt 作为 <see cref="AgentRun.UserMessage"/>、agent 回复作为 <see cref="AgentEventType.TextChunk"/> 事件
/// 落库到 <see cref="IConversationStore"/>（一个 run + 其 events），于是定时运行自然出现在「最近会话」，
/// 并在 App 打开期间通过 <see cref="ICustomAgentRunner"/> 的按会话缓存保留跨次记忆。业务层不依赖 AI 平台，故实现住宿主 VM 库。
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
        var conv = await _conversationStore.GetConversationAsync(conversationId, cancellationToken);
        if (conv is null)
        {
            conv = new Conversation
            {
                ConversationId = conversationId,
                AgentId = agentId,
                AgentName = agentName,
                Title = string.IsNullOrWhiteSpace(taskName) ? "定时任务" : taskName,
            };
            await _conversationStore.SaveConversationAsync(conv, cancellationToken);
        }

        // 计算下一个 run 序号（按已有 run 的最大 seq + 1）。
        var runs = await _conversationStore.GetRunsAsync(conversationId, cancellationToken);
        var seq = runs.Count == 0 ? 1 : runs.Max(r => r.Seq) + 1;

        // 流式运行（与聊天页相同的运行器路径），累积完整回复。
        var buffer = new StringBuilder();
        var startedAt = DateTimeOffset.UtcNow;
        RunStatus status;
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
            status = RunStatus.Completed;
        }
        catch (Exception ex)
        {
            buffer.Append($"\n\n[运行失败：{ex.Message}]");
            status = RunStatus.Failed;
        }

        var reply = buffer.ToString();
        var finishedAt = DateTimeOffset.UtcNow;

        // 持久化为一个 run + 单条 TextChunk 事件（回复文本）。与旧「user+assistant 两条 Message」语义等价。
        var run = new AgentRun
        {
            ConversationId = conversationId,
            Seq = seq,
            Status = status,
            AgentId = agentId,
            AgentName = agentName,
            UserMessage = userMessage,
            StartedAt = startedAt,
            FinishedAt = finishedAt,
        };
        var events = new List<AgentEvent>
        {
            new()
            {
                RunId = run.Id,
                Seq = 1,
                EventType = AgentEventType.TextChunk,
                Payload = AgentEventPayload.Serialize(new AgentEventPayload.TextChunk(reply)),
            }
        };
        await _conversationStore.AppendRunAsync(run, events, cancellationToken);

        return reply;
    }
}

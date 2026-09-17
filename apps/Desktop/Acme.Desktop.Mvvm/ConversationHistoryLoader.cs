using Aether.AI.Core.Conversations.Runs;
using Aether.AI.Memory;

namespace Acme.Desktop.Mvvm;

/// <summary>历史加载结果：全部重建轮次 + 该会话下一个 run 序号（max(Seq)+1；空会话=1）。</summary>
public sealed record ConversationHistoryResult(List<ChatTurnItem> Turns, int NextRunSeq);

/// <summary>
/// 会话历史加载器：从 ChatWorkspaceViewModel 抽出（2026-09 ChatVM 拆分 B4），方法体逐字搬入。
/// 从 typed 事件流重建 transcript：每条 run 一轮（user 消息在 run.UserMessage），events 还原思考/工具/答案/计划卡。
/// <para>VM 留 <c>Task.Run</c> 包裹（离 UI 线程保证）、<c>IsLoadingHistory</c> 守卫、<c>alreadyLoaded</c> 短路、
/// 分批渲染常量与 <c>PendingHistoryTurns</c> 暂存——那些是 UI 策略；本类只做纯 DB 读 + 重建。</para>
/// </summary>
public sealed class ConversationHistoryLoader
{
    private readonly IConversationStore _store;

    public ConversationHistoryLoader(IConversationStore store)
    {
        _store = store;
    }

    /// <summary>加载并重建整个会话 transcript。返回 null = 会话不存在（调用方据此把 NextRunSeq 归 1）。</summary>
    public async Task<ConversationHistoryResult?> LoadAsync(Guid conversationId, string? agentName)
    {
        var conv = await _store.GetConversationAsync(conversationId);
        if (conv is null) return null;
        var runs = await _store.GetRunsAsync(conversationId);
        var eventsByRun = new Dictionary<Guid, IReadOnlyList<AgentEvent>>();
        foreach (var run in runs)
            eventsByRun[run.Id] = await _store.GetEventsAsync(run.Id);
        var seq = runs.Count == 0 ? 1 : runs.Max(r => r.Seq) + 1;
        // 从 typed 事件流重建 transcript：每条 run 一轮（user 消息在 run.UserMessage），events 还原思考/工具/答案/计划卡。
        var all = TurnRebuilder.BuildTurnsFromRuns(runs, eventsByRun, agentName ?? "专家");
        return new ConversationHistoryResult(all, seq);
    }
}

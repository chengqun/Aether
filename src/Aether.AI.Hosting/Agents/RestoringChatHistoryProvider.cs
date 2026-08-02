using System.Collections.Concurrent;
using Aether.AI.Harness.Agents;
using Aether.AI.Memory;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace Aether.AI.Hosting.Agents;

/// <summary>
/// 会话真恢复：继承 <see cref="ChatHistoryProvider"/>，首次从 DB 加载历史（跨重启恢复），
/// 后续轮次靠内存累积（和 <see cref="InMemoryChatHistoryProvider"/> 行为一致）。
/// <para>关键：<see cref="StoreChatHistoryAsync"/> 不再 no-op——正确保存 per-session 消息列表，
/// TodoProvider / AgentModeProvider 的状态管理依赖此机制。</para>
/// </summary>
public sealed class RestoringChatHistoryProvider : ChatHistoryProvider
{
    private readonly IConversationStore? _store;
    private readonly SessionConversationMap _sessionMap;
    private readonly IChatReducer? _reducer;
    private readonly ILoggerFactory? _loggerFactory;
    // per-session 内存缓存（和 InMemoryChatHistoryProvider 一样）
    private readonly ConcurrentDictionary<AgentSession, List<ChatMessage>> _sessionMessages = new();
    private readonly HashSet<AgentSession> _restoredSessions = new();

    public RestoringChatHistoryProvider(IConversationStore? store, SessionConversationMap sessionMap,
        IChatReducer? reducer = null, ILoggerFactory? loggerFactory = null)
    {
        _store = store;
        _sessionMap = sessionMap ?? throw new ArgumentNullException(nameof(sessionMap));
        _reducer = reducer;
        _loggerFactory = loggerFactory;
    }

    protected override async ValueTask<IEnumerable<ChatMessage>> ProvideChatHistoryAsync(InvokingContext context, CancellationToken cancellationToken)
    {
        // 首次：从 DB 恢复历史，存入内存缓存
        if (_store is not null && _sessionMap.TryGet(context.Session, out var conversationId) && !_restoredSessions.Contains(context.Session))
        {
            try
            {
                var conv = await _store.GetAsync(conversationId, cancellationToken).ConfigureAwait(false);
                if (conv is not null)
                {
                    var messages = TranscriptHistoryConverter.Convert(conv.Messages);
                    _sessionMessages[context.Session] = messages.ToList();
                }
                _restoredSessions.Add(context.Session);
            }
            catch (Exception ex)
            {
                _loggerFactory?.CreateLogger<RestoringChatHistoryProvider>()?.LogWarning(ex, "恢复会话历史失败，使用空历史。");
                _restoredSessions.Add(context.Session);
            }
        }

        // 从内存返回历史
        var list = _sessionMessages.GetOrAdd(context.Session, _ => new List<ChatMessage>());

        // 应用 reducer（SlidingWindow 截断）
        if (_reducer is not null)
            return await _reducer.ReduceAsync(list, cancellationToken).ConfigureAwait(false);
        return list;
    }

    /// <summary>
    /// 保存 per-session 消息列表（请求 + 响应）到内存。不再 no-op——MAF 的 TodoProvider/AgentModeProvider
    /// 依赖 StoreChatHistoryAsync 被正确调用来维护 session state。
    /// </summary>
    protected override ValueTask StoreChatHistoryAsync(InvokedContext context, CancellationToken cancellationToken)
    {
        if (context.Session is not null)
        {
            var list = _sessionMessages.GetOrAdd(context.Session, _ => new List<ChatMessage>());
            // 追加本轮的请求 + 响应消息
            if (context.RequestMessages is not null)
                list.AddRange(context.RequestMessages);
            if (context.ResponseMessages is not null)
                list.AddRange(context.ResponseMessages);
        }
        return default;
    }
}

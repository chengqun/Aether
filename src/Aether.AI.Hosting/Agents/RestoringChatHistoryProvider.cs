using Aether.AI.Harness.Agents;
using Aether.AI.Memory;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace Aether.AI.Hosting.Agents;

/// <summary>
/// 会话真恢复：<see cref="ChatHistoryProvider"/> 从 <see cref="IConversationStore"/> 按 conversationId 加载
/// 持久化 transcript，经 <see cref="TranscriptHistoryConverter"/> 还原为 <see cref="ChatMessage"/> 历史。
/// <para>session→conversationId 映射由 <see cref="SessionConversationMap"/> 提供（HarnessRuntime 建 session 时写入）。</para>
/// <para>无 store / 无映射 / 加载失败 → 返回空历史（退化为无历史，不阻断）。<see cref="StoreChatHistoryAsync"/> no-op
/// （<c>PersistTurnAsync</c> 已落库）。可选 <see cref="IChatReducer"/>（SlidingWindow）截断。</para>
/// </summary>
public sealed class RestoringChatHistoryProvider : ChatHistoryProvider
{
    private readonly IConversationStore? _store;
    private readonly SessionConversationMap _sessionMap;
    private readonly IChatReducer? _reducer;
    private readonly ILoggerFactory? _loggerFactory;

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
        try
        {
            if (_store is null || !_sessionMap.TryGet(context.Session, out var conversationId))
                return Array.Empty<ChatMessage>();

            var conv = await _store.GetAsync(conversationId, cancellationToken).ConfigureAwait(false);
            if (conv is null) return Array.Empty<ChatMessage>();

            var messages = TranscriptHistoryConverter.Convert(conv.Messages);
            if (_reducer is not null)
                messages = (await _reducer.ReduceAsync(messages, cancellationToken).ConfigureAwait(false)).ToList();
            return messages;
        }
        catch (Exception ex)
        {
            _loggerFactory?.CreateLogger<RestoringChatHistoryProvider>()?.LogWarning(ex, "恢复会话历史失败，返回空历史。");
            return Array.Empty<ChatMessage>();
        }
    }

    protected override ValueTask StoreChatHistoryAsync(InvokedContext context, CancellationToken cancellationToken) => default;
}

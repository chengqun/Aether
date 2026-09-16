using Aether.AI.Hosting;
using Aether.AI.Memory;

namespace Acme.Desktop.Mvvm;

/// <summary>
/// 会话标题的 LLM 生成与应用（持久化 + 侧栏项更新）。从 <c>ChatWorkspaceViewModel</c> 抽出——
/// 标题是自持关注点（titleService + store + UI 线程回写侧栏项），与聊天流式管线无关。
/// </summary>
public sealed class ConversationTitleApplier
{
    private readonly IConversationTitleService _titleService;
    private readonly IConversationStore _store;
    private readonly IUiDispatcher _dispatcher;

    public ConversationTitleApplier(
        IConversationTitleService titleService, IConversationStore store, IUiDispatcher dispatcher)
    {
        _titleService = titleService ?? throw new ArgumentNullException(nameof(titleService));
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
    }

    /// <summary>异步用 LLM 生成标题并更新会话（持久化 + 侧栏项）。失败静默——不影响主流程。</summary>
    public async Task ApplyAsync(
        Guid conversationId, string userMessage, string assistantReply,
        IList<ConversationItem> conversations)
    {
        if (conversationId == Guid.Empty) return;
        try
        {
            var title = await _titleService.GenerateTitleAsync(userMessage, assistantReply);
            if (string.IsNullOrWhiteSpace(title)) return;

            var conv = await _store.GetConversationAsync(conversationId);
            if (conv is null) return;
            conv.Title = title;
            await _store.SaveConversationAsync(conv);

            await _dispatcher.InvokeAsync(() =>
            {
                var item = conversations.FirstOrDefault(c => c.Id == conversationId);
                if (item is not null) item.Title = title;
            });
        }
        catch { /* 标题生成失败不影响主流程 */ }
    }
}

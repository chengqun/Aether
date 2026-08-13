using System;

namespace Acme.Mobile.Mvvm.Events;

/// <summary>
/// AI 会话标题经 LLM 自动生成完成时发布，由 AI tab 宿主订阅以更新抽屉历史列表项的显示名。
/// （镜像桌面端 <c>ChatWorkspaceViewModel.GenerateAndApplyTitleAsync</c> 后直接刷新侧栏的模式。）
/// </summary>
public class ConversationTitleUpdatedEvent
{
    public Guid ConversationId { get; }
    public string Title { get; }

    public ConversationTitleUpdatedEvent(Guid conversationId, string title)
    {
        ConversationId = conversationId;
        Title = title;
    }
}

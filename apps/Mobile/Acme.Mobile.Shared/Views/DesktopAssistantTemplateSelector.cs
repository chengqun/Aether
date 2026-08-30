using Acme.ChatSocial.Domain;
using Acme.Mobile.Mvvm.ViewModels;

namespace Acme.Mobile.Shared.Views;

/// <summary>
/// 桌面助手消息模板选择器：按 <see cref="ChatMessage.SenderId"/> 与
/// <see cref="DesktopAssistantViewModel.CachedUserId"/> 比较区分「我的」（右侧绿色，用户发指令）
/// /「桌面」（左侧，桌面 agent 的概览摘要/结果）。独立于社交聊天的
/// <see cref="HumanChatMessageTemplateSelector"/>，不依赖 <see cref="ChatDetailViewModel.CachedUserId"/> 静态。
/// </summary>
public class DesktopAssistantTemplateSelector : DataTemplateSelector
{
    public DataTemplate? MineTemplate { get; set; }
    public DataTemplate? DesktopTemplate { get; set; }

    protected override DataTemplate OnSelectTemplate(object item, BindableObject container)
    {
        if (item is not ChatMessage m)
            return DesktopTemplate!;

        var me = (container.BindingContext as DesktopAssistantViewModel)?.CachedUserId ?? Guid.Empty;
        return m.SenderId == me && me != Guid.Empty
            ? MineTemplate!
            : DesktopTemplate!;
    }
}

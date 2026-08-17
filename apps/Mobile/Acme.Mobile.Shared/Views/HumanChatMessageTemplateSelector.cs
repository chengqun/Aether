using Acme.ChatSocial.Domain;
using Acme.Mobile.Mvvm.ViewModels;

namespace Acme.Mobile.Shared.Views;

/// <summary>
/// 人类聊天（1:1/群聊）消息模板选择器：按 <see cref="ChatMessage.SenderId"/> 与
/// <see cref="ChatDetailViewModel.CachedUserId"/> 比较区分"我的"（右侧绿色）/"对方"（左侧白色 Markdown）。
/// 与 <see cref="ChatMessageTemplateSelector"/>（agent 聊天，按 <c>ChatMessageItem.Role</c>/<c>ItemType</c>）互补——
/// 人类聊天的 <see cref="ChatMessage"/> 是域类型，无 Role/ItemType 字段，故用 SenderId 判定。
/// 缓存 userId 在 <c>OnActivatedAsync</c> 中先于消息加载解析，故渲染时已就绪。
/// </summary>
public class HumanChatMessageTemplateSelector : DataTemplateSelector
{
    public DataTemplate? MineTemplate { get; set; }
    public DataTemplate? OtherTemplate { get; set; }
    /// <summary>远程 agent 进度心跳（MessageType=="Progress"）：muted 浅气泡 + 活动指示器，覆盖更新不堆积。</summary>
    public DataTemplate? ProgressTemplate { get; set; }

    protected override DataTemplate OnSelectTemplate(object item, BindableObject container)
    {
        if (item is not ChatMessage m)
            return OtherTemplate!;

        // Progress 心跳用独立模板（SenderId 判定之前——心跳来自桌面 agent，非社交对话角色）。
        if (string.Equals(m.MessageType, "Progress", StringComparison.OrdinalIgnoreCase))
            return ProgressTemplate ?? OtherTemplate!;

        var me = ChatDetailViewModel.CachedUserId;
        // 缓存未就绪（Guid.Empty）时全部按"对方"渲染——避免自己的消息被误判为对方的；
        // 实际上 OnActivatedAsync 先解析 userId 再加载消息，正常路径不会到这里。
        return m.SenderId == me && me != Guid.Empty
            ? MineTemplate!
            : OtherTemplate!;
    }
}

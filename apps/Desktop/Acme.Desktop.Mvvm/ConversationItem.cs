using System.Windows.Input;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Acme.Shell;

/// <summary>最近会话列表行（侧边栏）。</summary>
public sealed partial class ConversationItem : ObservableObject
{
    public Guid Id { get; init; }

    [ObservableProperty] private string _title = string.Empty;
    [ObservableProperty] private string _preview = string.Empty;
    [ObservableProperty] private string _displayTime = string.Empty;
    [ObservableProperty] private string _agentName = string.Empty;

    /// <summary>绑定的专家 Id（字符串 Guid）。</summary>
    public string AgentId { get; init; } = string.Empty;

    /// <summary>是否是当前激活会话（高亮）。</summary>
    [ObservableProperty] private bool _isCurrent;

    /// <summary>打开此会话的命令（由 VM 注入）。
    /// 直接绑在行上，避免在 Flyout/ContextMenu 弹出子树里用 AncestorType=Window 取不到 Window 的问题。</summary>
    public ICommand? ResumeCommand { get; set; }

    /// <summary>删除此会话的命令（由 VM 注入），同上。</summary>
    public ICommand? DeleteCommand { get; set; }
}

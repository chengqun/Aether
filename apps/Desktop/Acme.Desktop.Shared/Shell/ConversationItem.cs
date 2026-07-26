using CommunityToolkit.Mvvm.ComponentModel;

namespace Acme.Desktop.Shared.Shell;

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
}

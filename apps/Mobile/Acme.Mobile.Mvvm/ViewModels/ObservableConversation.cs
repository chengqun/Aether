using System;
using CommunityToolkit.Mvvm.ComponentModel;
using Acme.ChatSocial.Domain;
using ChatMessage = Acme.ChatSocial.Domain.ChatMessage;

namespace Acme.Mobile.Mvvm.ViewModels;

public partial class ObservableConversation : ObservableObject
{
    private readonly Conversation _source;

    public ObservableConversation(Conversation source)
    {
        _source = source;
        UnreadCount = source.UnreadCount;
        LastMessage = source.LastMessage;
        LastMessageTime = source.LastMessageTime;
    }

    public Guid Id => _source.Id;
    public int Type => _source.Type;
    public string? Name => _source.Name;
    public string? OtherUserName => _source.OtherUserName;
    public Guid? OtherUserId => _source.OtherUserId;
    public string? OtherDeviceId => _source.OtherDeviceId;
    public string? DeviceType => _source.DeviceType;
    public int MemberCount => _source.MemberCount;
    public string? Context => _source.Context;
    public string? AgentName => _source.AgentName;
    public AgentKind? Kind => (AgentKind?)_source.Kind;

    /// <summary>会话累计 token 展示文本（Aether Conversation 无此字段，手机端暂不展示）。</summary>
    public string? TokenInfo => null;

    /// <summary>是否有可展示的 token 信息（用于 XAML IsVisible 绑定）。</summary>
    public bool HasTokenInfo => false;

    /// <summary>
    /// 是否为 LLM Agent 类型（用于 XAML 绑定）
    /// </summary>
    public bool IsAgentKindAgent => Kind == AgentKind.Agent;

    /// <summary>
    /// 是否为 Workflow 类型（用于 XAML 绑定）
    /// </summary>
    public bool IsAgentKindWorkflow => Kind == AgentKind.Workflow;

    /// <summary>
    /// 是否为 RuleBased 类型（用于 XAML 绑定）
    /// </summary>
    public bool IsAgentKindRuleBased => Kind == AgentKind.RuleBased;

    [ObservableProperty]
    private int _unreadCount;

    [ObservableProperty]
    private string? _lastMessage;

    [ObservableProperty]
    private DateTime? _lastMessageTime;

    public string DisplayName => _source.Name ?? _source.OtherUserName ?? "未知会话";
    public string Initial => string.IsNullOrEmpty(DisplayName) ? "?" : DisplayName[..1];
    public string? IconSource => null;

    public void UpdateFromMessage(ChatMessage message)
    {
        UnreadCount++;
        LastMessage = message.Content;
        LastMessageTime = message.CreationTime;
    }

    public void ClearUnreadCount()
    {
        UnreadCount = 0;
    }

    public void AddUnreadMessages(int count)
    {
        UnreadCount += count;
    }

    public void UpdateFromSource(Conversation source)
    {
        _source.UnreadCount = source.UnreadCount;
        _source.LastMessage = source.LastMessage;
        _source.LastMessageTime = source.LastMessageTime;

        UnreadCount = source.UnreadCount;
        LastMessage = source.LastMessage;
        LastMessageTime = source.LastMessageTime;
    }

    /// <summary>AI 自动生成标题后更新显示名（首条消息 lazy-create 时先用 agent 名占位，
    /// LLM 生成完真正的标题后调此方法就地刷新抽屉列表项，无需重载）。</summary>
    public void UpdateTitle(string title)
    {
        if (string.IsNullOrWhiteSpace(title)) return;
        _source.Name = title;
        OnPropertyChanged(nameof(DisplayName));
        OnPropertyChanged(nameof(Initial));
    }
}
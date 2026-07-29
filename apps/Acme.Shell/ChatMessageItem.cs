using System;
using System.Collections.Generic;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Acme.Shell;

public enum ChatMessageSide
{
    Left,
    Right
}

public partial class ChatMessageItem : ObservableObject
{
    public ChatMessageSide Side { get; init; }

    public string ItemType { get; init; } = "text";

    public string Time { get; init; } = DateTime.Now.ToString("HH:mm:ss");

    public string Sender { get; init; } = string.Empty;

    [ObservableProperty]
    private string _content = string.Empty;

    [ObservableProperty]
    private string? _markdownContent;

    [ObservableProperty]
    private bool _isStreaming;

    [ObservableProperty]
    private string? _toolName;

    [ObservableProperty]
    private Dictionary<string, object>? _input;

    [ObservableProperty]
    private Dictionary<string, object>? _output;

    [ObservableProperty]
    private bool _isExpanded;

    [ObservableProperty]
    private bool _isStreamingExpanded = true;

    /// <summary>本条文本是"思考段"（agent 运行中途的推理），非最终回答。分段交错展示用。</summary>
    [ObservableProperty]
    private bool _isReasoning;

    public bool IsUser => Side == ChatMessageSide.Right;

    public bool IsText => ItemType == "text";

    public bool IsToolCall => ItemType == "tool_call";

    public bool IsToolResult => ItemType == "tool_result";

    public bool IsThinking => ItemType == "thinking";

    public bool IsUserText => IsUser && IsText;

    public bool IsAgentStreamingText => !IsUser && IsText && IsStreaming;

    /// <summary>已结束的思考段：等宽可折叠"思考"卡片（非最终回答）。</summary>
    public bool IsAgentThinkingCard => !IsUser && IsText && !IsStreaming && IsReasoning;

    /// <summary>最终回答：markdown 气泡（非思考段）。</summary>
    public bool IsAgentMarkdownText => !IsUser && IsText && !IsStreaming && !IsReasoning;

    partial void OnIsStreamingChanged(bool value)
    {
        OnPropertyChanged(nameof(IsAgentStreamingText));
        OnPropertyChanged(nameof(IsAgentMarkdownText));
        OnPropertyChanged(nameof(IsAgentThinkingCard));
    }

    partial void OnIsReasoningChanged(bool value)
    {
        OnPropertyChanged(nameof(IsAgentThinkingCard));
        OnPropertyChanged(nameof(IsAgentMarkdownText));
    }
}

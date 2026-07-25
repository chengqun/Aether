using System;
using System.Collections.Generic;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Acme.Desktop.Shared.Shell;

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

    public bool IsUser => Side == ChatMessageSide.Right;

    public bool IsText => ItemType == "text";

    public bool IsToolCall => ItemType == "tool_call";

    public bool IsToolResult => ItemType == "tool_result";

    public bool IsThinking => ItemType == "thinking";

    public bool IsUserText => IsUser && IsText;

    public bool IsAgentStreamingText => !IsUser && IsText && IsStreaming;

    public bool IsAgentMarkdownText => !IsUser && IsText && !IsStreaming;

    partial void OnIsStreamingChanged(bool value)
    {
        OnPropertyChanged(nameof(IsAgentStreamingText));
        OnPropertyChanged(nameof(IsAgentMarkdownText));
    }
}

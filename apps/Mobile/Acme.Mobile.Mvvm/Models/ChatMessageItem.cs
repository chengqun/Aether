using System;
using System.Collections.Generic;
using System.Text;
using System.Text.Json;
using System.Text.Encodings.Web;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Acme.Mobile.Mvvm.Models;

/// <summary>
/// 聊天消息项，用于显示聊天列表中的单条消息。
/// </summary>
public partial class ChatMessageItem : ObservableObject
{
    [ObservableProperty]
    private string _id = string.Empty;

    [ObservableProperty]
    private string _itemType = "text"; // text, tool_call, tool_result, thinking

    [ObservableProperty]
    private string _role = "user"; // user, assistant

    [ObservableProperty]
    private string _content = string.Empty;

    [ObservableProperty]
    private string? _markdownContent;

    [ObservableProperty]
    private string? _toolName;

    [ObservableProperty]
    private Dictionary<string, object>? _input;

    [ObservableProperty]
    private Dictionary<string, object>? _output;

    [ObservableProperty]
    private string _authorName = string.Empty;

    [ObservableProperty]
    private DateTime _timestamp;

    [ObservableProperty]
    private bool _isStreaming;

    [ObservableProperty]
    private bool _isExpanded;

    [ObservableProperty]
    private int _inputTokens;

    [ObservableProperty]
    private int _outputTokens;

    /// <summary>
    /// Token 用量信息，格式: "{InputTokens}→{OutputTokens} tokens"，仅在两者均 > 0 时显示。
    /// </summary>
    public string TokenInfo => (InputTokens > 0 || OutputTokens > 0)
        ? $"{InputTokens}→{OutputTokens} tokens"
        : string.Empty;

    partial void OnInputTokensChanged(int value) => OnPropertyChanged(nameof(TokenInfo));
    partial void OnOutputTokensChanged(int value) => OnPropertyChanged(nameof(TokenInfo));

    /// <summary>
    /// 是否为工具调用类型
    /// </summary>
    public bool IsToolCall => ItemType == "tool_call";

    /// <summary>
    /// 是否为工具结果类型
    /// </summary>
    public bool IsToolResult => ItemType == "tool_result";

    /// <summary>
    /// 是否为思考状态
    /// </summary>
    public bool IsThinking => ItemType == "thinking";

    /// <summary>
    /// 是否为用户消息
    /// </summary>
    public bool IsUser => Role == "user";

    /// <summary>
    /// 是否为当前用户发送的消息（用于右侧气泡布局）
    /// </summary>
    public bool IsMine => Role == "user";

    /// <summary>
    /// 是否为 AI 文本回复（非工具调用/结果/思考）
    /// </summary>
    public bool IsAssistantText => Role == "assistant" && ItemType == "text";

    /// <summary>
    /// 是否有 Markdown 内容
    /// </summary>
    public bool HasMarkdown => !string.IsNullOrEmpty(MarkdownContent);

    /// <summary>
    /// 是否支持复制（思考中的消息不支持）
    /// </summary>
    public bool CanCopy => ItemType != "thinking";

    /// <summary>
    /// 返回要复制到剪贴板的文本内容，根据消息类型格式化。
    /// 思考中的消息返回 null。
    /// </summary>
    public string? CopyContent => ItemType switch
    {
        "text" => Content,
        "tool_call" => FormatToolCallContent(),
        "tool_result" => FormatToolResultContent(),
        "thinking" => null,
        _ => Content
    };

    private static readonly JsonSerializerOptions _jsonOptions = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    private string? FormatToolCallContent()
    {
        var sb = new StringBuilder();
        sb.Append($"调用: {ToolName}");
        if (Input != null && Input.Count > 0)
        {
            sb.AppendLine();
            sb.Append(FormatDict(Input));
        }
        return sb.ToString();
    }

    private string? FormatToolResultContent()
    {
        var sb = new StringBuilder();
        sb.Append($"结果: {ToolName}");
        if (Output != null && Output.Count > 0)
        {
            sb.AppendLine();
            sb.Append(FormatDict(Output));
        }
        return sb.ToString();
    }

    private static string FormatDict(Dictionary<string, object> dict)
    {
        try
        {
            return JsonSerializer.Serialize(dict, _jsonOptions);
        }
        catch
        {
            return dict.ToString() ?? string.Empty;
        }
    }
}
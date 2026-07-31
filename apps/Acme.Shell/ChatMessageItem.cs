using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Acme.Shell;

public enum ChatMessageSide
{
    Left,
    Right
}

/// <summary>计划卡里的单条待办（完成项 UI 划横线）。</summary>
public partial class TodoItemVM : ObservableObject
{
    [ObservableProperty] private string _title = string.Empty;
    [ObservableProperty] private string? _description;
    [ObservableProperty] private bool _isComplete;
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

    /// <summary>工具调用 Id，用于把 call 与 result 配对到同一条消息。</summary>
    public string CallId { get; set; } = string.Empty;

    [ObservableProperty]
    private Dictionary<string, object>? _input;

    [ObservableProperty]
    private Dictionary<string, object>? _output;

    /// <summary>工具结果已返回（call+result 合并到一条，完成后置 true）。</summary>
    [ObservableProperty]
    private bool _isToolCompleted;

    [ObservableProperty]
    private bool _isExpanded;

    [ObservableProperty]
    private bool _isStreamingExpanded = true;

    /// <summary>本条文本是"思考段"（agent 运行中途的推理），非最终回答。分段交错展示用。</summary>
    [ObservableProperty]
    private bool _isReasoning;

    /// <summary>本段持有 TextReasoningContent（模型推理），与 TextContent 的思考/答案段区分；推理段永不翻为答案。</summary>
    [ObservableProperty]
    private bool _isReasoningContent;

    /// <summary>审批状态：0=待审 1=已批准 2=已拒绝。仅 ItemType="approval" 用。</summary>
    [ObservableProperty]
    private int _approvalState;

    /// <summary>计划卡的待办列表（仅 ItemType="plan" 用）。完成项 UI 划横线。</summary>
    public ObservableCollection<TodoItemVM> Todos { get; } = new();

    public bool IsUser => Side == ChatMessageSide.Right;

    public bool IsText => ItemType == "text";

    /// <summary>工具步骤（调用+结果合并为一条）。</summary>
    public bool IsToolStep => ItemType == "tool";

    /// <summary>计划卡（todos_* 调用渲染）。</summary>
    public bool IsPlanCard => ItemType == "plan";

    /// <summary>审批卡（工具调用需审批）。</summary>
    public bool IsApprovalCard => ItemType == "approval";

    public bool IsUserText => IsUser && IsText;

    public bool IsAgentStreamingText => !IsUser && IsText && IsStreaming;

    /// <summary>已结束的思考段：等宽可折叠"思考"卡片（非最终回答）。</summary>
    public bool IsAgentThinkingCard => !IsUser && IsText && !IsStreaming && IsReasoning;

    /// <summary>最终回答：markdown 气泡（非思考段）。</summary>
    public bool IsAgentMarkdownText => !IsUser && IsText && !IsStreaming && !IsReasoning;

    public bool IsApprovalPending => IsApprovalCard && ApprovalState == 0;
    public bool IsApprovalApproved => IsApprovalCard && ApprovalState == 1;
    public bool IsApprovalRejected => IsApprovalCard && ApprovalState == 2;

    /// <summary>输入一行摘要（折叠态展示），如 skill=hello-py  arguments=["张三"]。</summary>
    public string ArgSummary => Summarize(Input);

    /// <summary>输出一行摘要（折叠态展示）。</summary>
    public string ResultSummary => Summarize(Output);

    partial void OnInputChanged(Dictionary<string, object>? value) => OnPropertyChanged(nameof(ArgSummary));

    partial void OnOutputChanged(Dictionary<string, object>? value)
    {
        OnPropertyChanged(nameof(ResultSummary));
    }

    partial void OnApprovalStateChanged(int value)
    {
        OnPropertyChanged(nameof(IsApprovalPending));
        OnPropertyChanged(nameof(IsApprovalApproved));
        OnPropertyChanged(nameof(IsApprovalRejected));
    }

    private static string Summarize(Dictionary<string, object>? dict)
    {
        if (dict is null || dict.Count == 0) return string.Empty;
        var parts = new List<string>();
        foreach (var kv in dict)
            parts.Add($"{kv.Key}={FormatValue(kv.Value)}");
        var s = string.Join("  ", parts);
        return s.Length > 80 ? s[..80] + "…" : s;
    }

    private static string FormatValue(object? v)
    {
        if (v is null) return "null";
        if (v is string s) return s;
        if (v is System.Text.Json.JsonElement je)
        {
            return je.ValueKind switch
            {
                System.Text.Json.JsonValueKind.String => je.GetString() ?? string.Empty,
                System.Text.Json.JsonValueKind.Null => "null",
                _ => je.ToString(),
            };
        }
        return v.ToString() ?? string.Empty;
    }

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

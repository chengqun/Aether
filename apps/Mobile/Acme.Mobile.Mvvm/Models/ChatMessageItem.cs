using System;
using System.Collections.Generic;
using System.Text;
using System.Text.Json;
using System.Text.Encodings.Web;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Acme.Mobile.Mvvm.Models;

/// <summary>
/// 聊天消息项：对齐桌面 <c>ChatMessageItem</c> 的分段模型。
/// ItemType ∈ { text, tool, plan, approval }：
///   - text：答案段（<see cref="IsReasoning"/>=false）或思考段（<see cref="IsReasoning"/>=true，TextReasoningContent）。
///   - tool：单卡工具步（call+result 合并；<see cref="IsToolCompleted"/> result 到达置 true）。
///   - plan：计划卡（todos 快照，手机端暂未渲染）。
///   - approval：工具审批卡（runtime-only，不持久化）。
/// </summary>
public partial class ChatMessageItem : ObservableObject
{
    [ObservableProperty]
    private string _id = string.Empty;

    /// <summary>text / tool / plan / approval（对齐桌面分段模型）。</summary>
    [ObservableProperty]
    private string _itemType = "text";

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

    /// <summary>本条文本是"思考段"（agent 运行中途的推理 TextReasoningContent），非最终回答。分段交错展示用。</summary>
    [ObservableProperty]
    private bool _isReasoning;

    /// <summary>本段持有 TextReasoningContent（模型推理），与 TextContent 的思考/答案段区分；推理段永不翻为答案。</summary>
    [ObservableProperty]
    private bool _isReasoningContent;

    /// <summary>工具结果已返回（call+result 合并到一条，完成后置 true）。</summary>
    [ObservableProperty]
    private bool _isToolCompleted;

    /// <summary>工具调用的 CallId（MEAI FunctionCallContent.CallId）。审批恢复轮去重 + 持久化配对用。</summary>
    [ObservableProperty]
    private string? _callId;

    /// <summary>审批卡状态：0=等待审批，1=已批准，2=已拒绝/取消（仅 ItemType=="approval" 有效）。</summary>
    [ObservableProperty]
    private int _approvalState;

    // —— 派生布尔（驱动 XAML 模板选择 / 可见性）——

    /// <summary>文本段（答案或思考）。</summary>
    public bool IsText => ItemType == "text";

    /// <summary>工具步骤（调用+结果合并为一条）。</summary>
    public bool IsToolStep => ItemType == "tool";

    /// <summary>审批卡（工具调用需审批）。</summary>
    public bool IsApprovalCard => ItemType == "approval";

    /// <summary>用户消息。</summary>
    public bool IsUser => Role == "user";

    /// <summary>当前用户发送的消息（右侧气泡布局）。</summary>
    public bool IsMine => Role == "user";

    /// <summary>AI 文本回复（非工具/审批）。</summary>
    public bool IsAssistantText => Role == "assistant" && ItemType == "text";

    /// <summary>是否有 Markdown 内容。</summary>
    public bool HasMarkdown => !string.IsNullOrEmpty(MarkdownContent);

    /// <summary>是否支持复制。</summary>
    public bool CanCopy => IsText || IsToolStep;

    /// <summary>审批状态派生：等待。</summary>
    public bool IsApprovalPending => IsApprovalCard && ApprovalState == 0;
    /// <summary>审批状态派生：已批准。</summary>
    public bool IsApprovalApproved => IsApprovalCard && ApprovalState == 1;
    /// <summary>审批状态派生：已拒绝。</summary>
    public bool IsApprovalRejected => IsApprovalCard && ApprovalState == 2;

    partial void OnApprovalStateChanged(int value)
    {
        OnPropertyChanged(nameof(IsApprovalPending));
        OnPropertyChanged(nameof(IsApprovalApproved));
        OnPropertyChanged(nameof(IsApprovalRejected));
    }

    /// <summary>复制到剪贴板的文本：文本段返回 Content；工具步返回"调用/结果"汇总。</summary>
    public string? CopyContent => ItemType switch
    {
        "text" => Content,
        "tool" => FormatToolContent(),
        _ => Content
    };

    /// <summary>工具调用参数 JSON（卡片调用行展示）。Input 为空则 null。</summary>
    public string? InputDetail => IsToolStep && Input is { Count: > 0 } ? FormatDict(Input) : null;

    /// <summary>工具结果 JSON（卡片结果行展示，<see cref="IsToolCompleted"/> 后可见）。Output 为空则 null。</summary>
    public string? OutputDetail => IsToolStep && IsToolCompleted && Output is { Count: > 0 } ? FormatDict(Output) : null;

    /// <summary>是否有输入详情（驱动调用行可见性）。</summary>
    public bool HasInputDetail => !string.IsNullOrEmpty(InputDetail);

    /// <summary>是否有结果详情（驱动结果行可见性）。</summary>
    public bool HasOutputDetail => !string.IsNullOrEmpty(OutputDetail);

    partial void OnInputChanged(Dictionary<string, object>? value)
    {
        OnPropertyChanged(nameof(InputDetail));
        OnPropertyChanged(nameof(HasInputDetail));
    }

    partial void OnOutputChanged(Dictionary<string, object>? value)
    {
        OnPropertyChanged(nameof(OutputDetail));
        OnPropertyChanged(nameof(HasOutputDetail));
    }

    partial void OnIsToolCompletedChanged(bool value)
    {
        OnPropertyChanged(nameof(OutputDetail));
        OnPropertyChanged(nameof(HasOutputDetail));
    }

    private static readonly JsonSerializerOptions _jsonOptions = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    private string? FormatToolContent()
    {
        var sb = new StringBuilder();
        sb.Append($"调用: {ToolName}");
        if (Input is { Count: > 0 })
        {
            sb.AppendLine();
            sb.Append(FormatDict(Input));
        }
        if (IsToolCompleted && Output is { Count: > 0 })
        {
            sb.AppendLine();
            sb.Append("结果:");
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

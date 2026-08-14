using System;
using System.Collections.Generic;
using System.Text;
using System.Text.Json;
using System.Text.Encodings.Web;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Acme.Mobile.Mvvm.Models;

/// <summary>
/// 聊天消息项：对齐桌面 <c>ChatMessageItem</c> 的分段模型 + 工具卡紧凑摘要/类型化结果渲染。
/// ItemType ∈ { text, tool, plan, approval }：
///   - text：答案段（<see cref="IsReasoning"/>=false）或思考段（<see cref="IsReasoning"/>=true，TextReasoningContent）。
///   - tool：单卡工具步（call+result 合并；<see cref="IsToolCompleted"/> result 到达置 true）。
///     折叠态一行摘要 <see cref="VerbIcon"/> + <see cref="ToolName"/> + <see cref="MainArg"/> + ✓ + ▸，
///     展开态按 <see cref="ResultKind"/> 类型化渲染结果（text/code/table/error/json）。
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

    /// <summary>结果渲染类型：text/code/table/error/json。由 <see cref="NormalizeToolResult"/> 据 Output 归一。</summary>
    [ObservableProperty]
    private string _resultKind = "text";

    /// <summary>从 Output 归一出的主结果文本（取 result/output/content/text/value/message/data 键的字符串值）。</summary>
    [ObservableProperty]
    private string? _resultText;

    /// <summary>结果是否超长（&gt;20 行），控制默认折叠 + 限高。</summary>
    [ObservableProperty]
    private bool _isResultLong;

    /// <summary>结果行数（用于"· 142 行"徽标）。</summary>
    [ObservableProperty]
    private int _resultLineCount;

    /// <summary>流式思考段是否展开（流式中可折叠，默认展开）。完成态用 <see cref="IsExpanded"/>。</summary>
    [ObservableProperty]
    private bool _isStreamingExpanded = true;

    /// <summary>流式停止后填入的耗时摘要（如 "2s"，含 s 后缀），仅展示用。对齐桌面推理卡"思考 · 2s"。</summary>
    [ObservableProperty]
    private string? _elapsedText;

    /// <summary>段创建时刻，用于计算推理/答案耗时。非 observable（纯展示用）。</summary>
    private DateTime _startedAt = DateTime.Now;

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

    /// <summary>思考卡当前是否展开（body 可见 + ▾ 指示）。
    /// 流式段看 <see cref="IsStreamingExpanded"/>（默认展开，可折叠）；完成段看 <see cref="IsExpanded"/>（默认折叠）。
    /// 对齐桌面：流式思考 / 完成思考 都是可折叠 pill。</summary>
    public bool IsReasoningExpanded => IsReasoning && (IsStreaming ? IsStreamingExpanded : IsExpanded);

    /// <summary>结果渲染分支（按 ResultKind 挑模板，XAML 用 IsVisible 绑定）。</summary>
    public bool IsResultText => IsToolStep && ResultKind == "text";
    public bool IsResultCode => IsToolStep && ResultKind == "code";
    public bool IsResultError => IsToolStep && ResultKind == "error";
    public bool IsResultTable => IsToolStep && ResultKind == "table";
    public bool IsResultJson => IsToolStep && ResultKind == "json";

    /// <summary>输入一行摘要（折叠态展示），如 skill=hello-py  arguments=["张三"]。</summary>
    public string ArgSummary => Summarize(Input);

    /// <summary>输出一行摘要（折叠态展示）。</summary>
    public string ResultSummary => Summarize(Output);

    /// <summary>折叠态结果预览：结果首行截断 60 字（无需展开即可看到工具产出）。</summary>
    public string ResultPreview
    {
        get
        {
            if (string.IsNullOrEmpty(ResultText)) return string.Empty;
            var first = ResultText.Split('\n')[0].Trim();
            return first.Length > 60 ? first[..60] + "…" : first;
        }
    }

    public bool HasResultPreview => IsToolCompleted && !string.IsNullOrEmpty(ResultPreview);

    /// <summary>工具动词图标（按 ToolName 关键字映射：📖读/✏写/▶命令/🔎查询/📈行情/🔏下单/🧩技能…）。</summary>
    public string VerbIcon => VerbFor(ToolName);

    /// <summary>主参数（折叠态"动词+对象"一行用）：按 path/file/command/city/symbol… 优先键取首个标量，截断 30 字。</summary>
    public string MainArg => PickMainArg(Input);

    partial void OnApprovalStateChanged(int value)
    {
        OnPropertyChanged(nameof(IsApprovalPending));
        OnPropertyChanged(nameof(IsApprovalApproved));
        OnPropertyChanged(nameof(IsApprovalRejected));
    }

    partial void OnInputChanged(Dictionary<string, object>? value)
    {
        OnPropertyChanged(nameof(ArgSummary));
        OnPropertyChanged(nameof(MainArg));
        OnPropertyChanged(nameof(InputDetail));
        OnPropertyChanged(nameof(HasInputDetail));
    }

    partial void OnOutputChanged(Dictionary<string, object>? value)
    {
        OnPropertyChanged(nameof(ResultSummary));
        NormalizeToolResult(value);
    }

    partial void OnResultKindChanged(string value)
    {
        OnPropertyChanged(nameof(IsResultText));
        OnPropertyChanged(nameof(IsResultCode));
        OnPropertyChanged(nameof(IsResultError));
        OnPropertyChanged(nameof(IsResultTable));
        OnPropertyChanged(nameof(IsResultJson));
    }

    partial void OnResultTextChanged(string? value)
    {
        OnPropertyChanged(nameof(ResultPreview));
        OnPropertyChanged(nameof(HasResultPreview));
    }

    partial void OnIsToolCompletedChanged(bool value)
    {
        OnPropertyChanged(nameof(HasResultPreview));
        OnPropertyChanged(nameof(OutputDetail));
        OnPropertyChanged(nameof(HasOutputDetail));
    }

    /// <summary>流式停止时计算耗时摘要（供完成思考卡"思考 · 2s"展示）。</summary>
    partial void OnIsStreamingChanged(bool value)
    {
        if (!value) ElapsedText = $"{(DateTime.Now - _startedAt).TotalSeconds:F0}s";
        OnPropertyChanged(nameof(IsReasoningExpanded));
    }

    partial void OnIsStreamingExpandedChanged(bool value) => OnPropertyChanged(nameof(IsReasoningExpanded));

    partial void OnIsExpandedChanged(bool value) => OnPropertyChanged(nameof(IsReasoningExpanded));

    partial void OnIsReasoningChanged(bool value) => OnPropertyChanged(nameof(IsReasoningExpanded));

    /// <summary>点击工具卡切换展开/折叠（XAML TapGestureRecognizer 绑定）。</summary>
    [RelayCommand]
    private void ToggleExpand() => IsExpanded = !IsExpanded;

    /// <summary>点击思考卡切换展开/折叠：流式段切 <see cref="IsStreamingExpanded"/>，完成段切 <see cref="IsExpanded"/>。</summary>
    [RelayCommand]
    private void ToggleReasoning()
    {
        if (IsStreaming) IsStreamingExpanded = !IsStreamingExpanded;
        else IsExpanded = !IsExpanded;
    }

    /// <summary>
    /// 据 Output 字典归一结果展示信息：取主结果字符串（result/output/content/text/value/message/data 键），
    /// 判 <see cref="ResultKind"/>（含换行→code；短串含 error/失败/异常→error；多键 dict→table；否则 json 兜底）。
    /// 超过 20 行置 <see cref="IsResultLong"/>。由各工具结果渲染模板消费。
    /// </summary>
    private void NormalizeToolResult(Dictionary<string, object>? output)
    {
        ResultKind = "text"; ResultText = null; IsResultLong = false; ResultLineCount = 0;
        if (output is null || output.Count == 0) return;

        string? main = null;
        foreach (var key in new[] { "result", "output", "content", "text", "value", "message", "data" })
        {
            if (output.TryGetValue(key, out var v) && ToStr(v) is { } s && s.Length > 0) { main = s; break; }
        }

        if (main is not null)
        {
            ResultText = main;
            ResultLineCount = main.Count(c => c == '\n') + 1;
            bool hasError = main.Contains("error", StringComparison.OrdinalIgnoreCase)
                            || main.Contains("失败") || main.Contains("异常");
            if (hasError && main.Length < 300) ResultKind = "error";
            else if (main.Contains('\n')) ResultKind = "code";
            else ResultKind = "text";
            IsResultLong = ResultLineCount > 20;
        }
        else if (output.Count > 1)
        {
            ResultKind = "table";
        }
        else
        {
            ResultKind = "json"; // 兜底
        }
    }

    private static string? ToStr(object? v) => v switch
    {
        null => null,
        string s => s,
        JsonElement je => je.ValueKind == JsonValueKind.String
            ? je.GetString()
            : (je.ValueKind == JsonValueKind.Null ? null : je.ToString()),
        _ => v.ToString(),
    };

    /// <summary>主参数：优先键取首个标量，截断 30 字（折叠态"ToolName(主参数)"用）。</summary>
    private static string PickMainArg(Dictionary<string, object>? input)
    {
        if (input is null || input.Count == 0) return string.Empty;
        foreach (var key in new[] { "path", "file", "command", "city", "symbol", "query", "name", "title", "id", "input" })
        {
            if (input.TryGetValue(key, out var v) && ToStr(v) is { } s && s.Length > 0)
                return s.Length > 30 ? s[..30] + "…" : s;
        }
        foreach (var kv in input)
        {
            if (ToStr(kv.Value) is { } s2 && s2.Length > 0)
                return s2.Length > 30 ? s2[..30] + "…" : s2;
        }
        return string.Empty;
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
        if (v is JsonElement je)
        {
            return je.ValueKind switch
            {
                JsonValueKind.String => je.GetString() ?? string.Empty,
                JsonValueKind.Null => "null",
                _ => je.ToString(),
            };
        }
        return v.ToString() ?? string.Empty;
    }

    /// <summary>按工具名关键字映射动词图标（主流 agent 风"动词+对象"一行）。</summary>
    private static string VerbFor(string? name)
    {
        if (string.IsNullOrEmpty(name)) return "🔧";
        var n = name.ToLowerInvariant();
        if (n.Contains("skill")) return "🧩";
        if (n.Contains("note")) return "📝";
        if (n.Contains("backtest")) return "▶";
        if (n.Contains("apk")) return "📦";
        if (n.Contains("order") || n.Contains("place") || n.Contains("trade")) return "🔏";
        if (n.Contains("quote") || n.Contains("market") || n.Contains("kline") || n.Contains("bar") || n.EndsWith("md")) return "📈";
        if (n.Contains("search") || n.Contains("query") || n.StartsWith("get") || n.StartsWith("fetch")) return "🔎";
        if (n.Contains("read") || n.Contains("file")) return "📖";
        if (n.Contains("write") || n.Contains("edit") || n.Contains("create") || n.Contains("update") || n.Contains("delete") || n.Contains("remove")) return "✏";
        if (n.Contains("bash") || n.Contains("shell") || n.Contains("command") || n.Contains("run")) return "▶";
        return "🔧";
    }

    /// <summary>复制到剪贴板的文本：文本段返回 Content；工具步返回"调用/结果"汇总。</summary>
    public string? CopyContent => ItemType switch
    {
        "text" => Content,
        "tool" => FormatToolContent(),
        _ => Content
    };

    /// <summary>工具调用参数 JSON（展开态输入区展示）。Input 为空则 null。</summary>
    public string? InputDetail => IsToolStep && Input is { Count: > 0 } ? FormatDict(Input) : null;

    /// <summary>工具结果 JSON（展开态 table/json 兜底渲染，<see cref="IsToolCompleted"/> 后可见）。Output 为空则 null。</summary>
    public string? OutputDetail => IsToolStep && IsToolCompleted && Output is { Count: > 0 } ? FormatDict(Output) : null;

    /// <summary>是否有输入详情（驱动展开态输入区可见性）。</summary>
    public bool HasInputDetail => !string.IsNullOrEmpty(InputDetail);

    /// <summary>是否有结果详情（驱动展开态 table/json 兜底可见性）。</summary>
    public bool HasOutputDetail => !string.IsNullOrEmpty(OutputDetail);

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

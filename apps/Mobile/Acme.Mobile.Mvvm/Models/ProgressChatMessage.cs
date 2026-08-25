using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows.Input;
using Aether.AI.Core.Agents;
using Acme.ChatSocial.Domain;

namespace Acme.Mobile.Mvvm.Models;

/// <summary>
/// 手机端富进度气泡模型：把桌面发来的 <see cref="RunProgress"/> JSON（messageType="Progress"）
/// 解析后按 kind 富渲染（与桌宠同款：图标/spinner/计划逐条 ✅⏳/终态）。
/// <para>子类 <see cref="ChatMessage"/>：适配既有 <c>ObservableCollection&lt;ChatMessage&gt;</c> +
/// 选择器 <c>is ChatMessage</c> 守卫，经现有 replace-or-add（<c>Messages[idx]=pcm</c>）重渲染——
/// ChatMessage 是 POCO 非 INPC，原地改属性不刷新 UI，故 VM 须 Replace。</para>
/// <para>POCO 非 INPC：所有展示字段在 <see cref="TryParse"/> 工厂里一次算定，靠 Replace 触发刷新。
/// <see cref="ApproveCommand"/>/<see cref="RejectCommand"/> 由 VM 注入（仅 ApprovalRequested kind）。</para>
/// </summary>
public class ProgressChatMessage : ChatMessage
{
    /// <summary>反序列化选项：camelCase 不敏感匹配（桌面发 camelCase）+ 枚举字符串。</summary>
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() },
    };

    /// <summary>原始结构信号（降级时 null）。</summary>
    public RunProgress? Progress { get; set; }

    /// <summary>kind→emoji 图标（降级时 null→隐藏）。</summary>
    public string? Icon { get; set; }

    /// <summary>复合展示标签（降级时=Content 原文，保证总有东西显）。</summary>
    public string DisplayLabel { get; set; } = string.Empty;

    /// <summary>是否进行中（PlanUpdated/ApprovalRequested→spinner 转；终态→停）。</summary>
    public bool IsInProgress { get; set; }

    /// <summary>计划 markdown（PlanUpdated 时预格式化的 ✅/⏳ 多行串；其它 null）。</summary>
    public string? PlanMarkdown { get; set; }

    /// <summary>是否有计划 markdown（驱动 MarkdownView 显隐）。</summary>
    public bool HasTodos => PlanMarkdown is not null;

    /// <summary>是否为审批请求（驱动批准/拒绝按钮显隐）。</summary>
    public bool IsApproval { get; set; }

    /// <summary>批准命令（VM 注入，仅 ApprovalRequested）。set-once POCO，靠 Replace 刷新。</summary>
    public ICommand? ApproveCommand { get; set; }

    /// <summary>拒绝命令（VM 注入，仅 ApprovalRequested）。</summary>
    public ICommand? RejectCommand { get; set; }

    /// <summary>解析桌面发来的 RunProgress JSON。成功按 kind 算展示字段；失败降级显原文一行（不崩）。
    /// 成功返回 true；降级返回 false（pcm 仍非 null，可显示）。</summary>
    public static bool TryParse(string content, out ProgressChatMessage pcm)
    {
        pcm = new ProgressChatMessage { Content = content };
        RunProgress? progress = null;
        try
        {
            progress = JsonSerializer.Deserialize<RunProgress>(content, JsonOpts);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[ProgressChatMessage] 解析失败（降级显原文）: {ex.Message}");
        }
        if (progress is null)
        {
            // 降级：旧桌面发的纯文本心跳也能显一行
            pcm.DisplayLabel = content;
            return false;
        }

        pcm.Progress = progress;
        pcm.Icon = progress.Kind switch
        {
            RunProgressKind.Reasoning => "💭",
            RunProgressKind.ToolStarted => "🔧",
            RunProgressKind.ToolFinished => "✅",
            RunProgressKind.ApprovalRequested => "⛔",
            RunProgressKind.PlanUpdated => "📋",
            RunProgressKind.RunDone => "🏁",
            RunProgressKind.RunError => "⚠️",
            _ => null,
        };
        pcm.DisplayLabel = progress.Kind switch
        {
            RunProgressKind.PlanUpdated => $"计划 {progress.PlanStepCount} 步",
            RunProgressKind.ApprovalRequested => $"需要你确认「{progress.ToolName}」",
            RunProgressKind.RunDone => "已完成",
            RunProgressKind.RunError => $"出错了：{progress.Text}",
            _ => content,
        };
        pcm.IsInProgress = progress.Kind is RunProgressKind.PlanUpdated or RunProgressKind.ApprovalRequested;
        pcm.IsApproval = progress.Kind == RunProgressKind.ApprovalRequested;
        if (progress.Kind == RunProgressKind.PlanUpdated && progress.Todos is { Count: > 0 })
        {
            var sb = new System.Text.StringBuilder();
            foreach (var t in progress.Todos)
                sb.Append(t.IsComplete ? "✅ " : "⏳ ").AppendLine(t.Title);
            pcm.PlanMarkdown = sb.ToString().TrimEnd();
        }
        return true;
    }
}

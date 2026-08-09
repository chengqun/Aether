using System.ComponentModel;
using System.Text.Json;
using Microsoft.Extensions.AI;

namespace Acme.Mobile.Shared.Services;

/// <summary>
/// 手机端 <c>ask_user</c> 工具（镜像桌面 <c>AskUserTool</c>）。
/// Stage 1：返回 <c>[]</c>（用户取消语义）——agent 收到空答案后通常自行回退到默认行为。
/// Stage 2：接 <c>Application.Current.MainPage.DisplayActionSheet</c> / 自定义弹层做真实交互。
/// </summary>
public sealed class MobileAskUserTool
{
    [Description("向用户提出一个或多个确认/选择问题，阻塞等待全部回答。用于需要用户拍板/澄清/选择时，" +
                 "而非直接纯文本提问。多个问题一次性传入。返回 JSON 数组（按提问顺序的回答）；用户取消返回 []。")]
    public async Task<string> AskAsync(
        [Description("问题列表，每项 {question, options}。options 为可选选项数组，为空则该问自由文本回答")]
        AskUserQuestionInput[] questions)
    {
        // Stage 1 stub：无 UI 弹层，返回取消。Stage 2 替换为真实表单。
        await Task.CompletedTask;
        return "[]";
    }
}

/// <summary>单个待确认问题（与桌面 Acme.Desktop.Mvvm.AskUserQuestionInput 同构，手机端本地定义以免引 Acme.Desktop.Mvvm）。</summary>
public sealed class AskUserQuestionInput
{
    public string Question { get; set; } = string.Empty;
    public IReadOnlyList<string> Options { get; set; } = Array.Empty<string>();
}

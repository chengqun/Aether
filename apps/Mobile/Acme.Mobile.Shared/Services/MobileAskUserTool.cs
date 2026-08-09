using System.ComponentModel;
using System.Text.Json;
using Microsoft.Extensions.AI;
using Microsoft.Maui;
using Microsoft.Maui.ApplicationModel;

namespace Acme.Mobile.Shared.Services;

/// <summary>
/// 手机端 <c>ask_user</c> 工具（镜像桌面 <c>AskUserTool</c>）。
/// agent 需要用户拍板/澄清时调用，逐问题弹 <see cref="Page.DisplayActionSheet"/>（选项）/
/// <see cref="Page.DisplayPromptAsync"/>（自由文本），阻塞等待全部回答。返回 JSON 数组（按提问顺序）；
/// 任意问题用户取消 → 整体返回 <c>[]</c>（取消语义）。执行体 marshal 到 UI 线程（ask_user 在 agent 后台线程被调）。
/// </summary>
public sealed class MobileAskUserTool
{
    [Description("向用户提出一个或多个确认/选择问题，阻塞等待全部回答。用于需要用户拍板/澄清/选择时，" +
                 "而非直接纯文本提问。多个问题一次性传入。返回 JSON 数组（按提问顺序的回答）；用户取消返回 []。")]
    public async Task<string> AskAsync(
        [Description("问题列表，每项 {question, options}。options 为可选选项数组，为空则该问自由文本回答")]
        AskUserQuestionInput[] questions)
    {
        var qs = questions ?? Array.Empty<AskUserQuestionInput>();
        if (qs.Length == 0) return "[]";

        var page = Application.Current?.MainPage;
        if (page == null) return "[]";

        var answers = new List<string>(qs.Length);
        foreach (var q in qs)
        {
            // ask_user 在 agent 后台线程被调；DisplayActionSheet/Prompt 必须在 UI 线程。
            string? answer = await MainThread.InvokeOnMainThreadAsync(async () =>
            {
                if (q.Options is { Count: > 0 })
                {
                    // 选项题：DisplayActionSheetAsync 返回所选按钮 text，取消/Dismiss 返回 null
                    var choice = await page.DisplayActionSheetAsync(q.Question, "取消", null, q.Options.ToArray());
                    return choice;
                }
                else
                {
                    // 自由文本题：DisplayPromptAsync 返回输入 text，取消返回 null
                    return await page.DisplayPromptAsync(q.Question, q.Question, "确定", "取消", "请输入");
                }
            }).ConfigureAwait(false);

            // 任意一问取消 → 整体取消
            if (answer is null) return "[]";
            answers.Add(answer);
        }

        return JsonSerializer.Serialize(answers);
    }
}

/// <summary>单个待确认问题（与桌面 Acme.Desktop.Mvvm.AskUserQuestionInput 同构，手机端本地定义以免引 Acme.Desktop.Mvvm）。</summary>
public sealed class AskUserQuestionInput
{
    public string Question { get; set; } = string.Empty;
    public IReadOnlyList<string> Options { get; set; } = Array.Empty<string>();
}

using System.ComponentModel;
using System.Text.Json;
using Acme.Desktop.Mvvm;
using Microsoft.Extensions.AI;

namespace Acme.Desktop.Shared.Shell;

/// <summary>
/// <c>ask_user</c> 工具：agent 需要用户拍板/澄清时调用，弹交互表单（支持一次问多个问题），
/// 返回 JSON 数组（按提问顺序的回答）。替代纯文本提问 + 停轮。执行体在 UI 线程弹模态窗并 await。
/// </summary>
public sealed class AskUserTool
{
    private readonly IAskUserDialogService _dialog;

    public AskUserTool(IAskUserDialogService dialog) => _dialog = dialog;

    [Description("向用户提出一个或多个确认/选择问题，阻塞等待全部回答。用于需要用户拍板/澄清/选择时，" +
                 "而非直接纯文本提问。多个问题一次性传入（表单一次提交），不要逐个调用。" +
                 "返回 JSON 数组，按提问顺序给出每个的回答（用户选的选项或自定义文本）；用户取消返回 []。")]
    public async Task<string> AskAsync(
        [Description("问题列表，每项 {question, options}。options 为可选选项数组，为空则该问自由文本回答")] AskUserQuestionInput[] questions)
    {
        var qs = questions ?? Array.Empty<AskUserQuestionInput>();
        var answers = await _dialog.ShowAsync(qs).ConfigureAwait(false);
        return answers is null ? "[]" : JsonSerializer.Serialize(answers);
    }
}

using System.ComponentModel;
using Acme.Shell;
using Microsoft.Extensions.AI;

namespace Acme.Desktop.Shared.Shell;

/// <summary>
/// <c>ask_user</c> 工具：agent 需要用户拍板/澄清时调用，弹出交互对话框，返回用户的选择或自由文本。
/// 替代纯文本提问 + 停轮（plan 模式交互式确认的"对话框化"）。执行体在 UI 线程弹模态窗并 await 结果。
/// </summary>
public sealed class AskUserTool
{
    private readonly IAskUserDialogService _dialog;

    public AskUserTool(IAskUserDialogService dialog) => _dialog = dialog;

    [Description("向用户提问或请求确认，阻塞等待用户回答。用于需要用户拍板/澄清/选择时，而非直接纯文本提问。" +
                 "返回用户选择的选项或自定义回答文本。")]
    public async Task<string> AskAsync(
        [Description("要问用户的问题/确认内容（完整描述，如\"相似度阈值用 ≤3 还是 ≤2？\"）")] string question,
        [Description("供用户选择的选项列表（如 [\"≤3\",\"≤2\",\"≤4\"]）；为空则用户自由文本回答")] string[] options)
    {
        var opts = options ?? Array.Empty<string>();
        return await _dialog.ShowAsync(question, opts).ConfigureAwait(false) ?? string.Empty;
    }
}

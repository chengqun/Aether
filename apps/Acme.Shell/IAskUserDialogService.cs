namespace Acme.Shell;

/// <summary>
/// 弹出提问/确认对话框，返回用户的选择或自由文本回答；用户取消返回 <c>null</c>。
/// 供 <c>ask_user</c> 工具在 agent 需要用户拍板/澄清时调用（替代纯文本提问）。UI 无关接口。
/// </summary>
public interface IAskUserDialogService
{
    Task<string?> ShowAsync(string question, IReadOnlyList<string> options);
}

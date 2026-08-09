namespace Acme.Desktop.Mvvm;

/// <summary>单个待确认问题（问题文本 + 可选选项）。</summary>
public sealed class AskUserQuestionInput
{
    public string Question { get; set; } = string.Empty;
    public IReadOnlyList<string> Options { get; set; } = Array.Empty<string>();
}

/// <summary>
/// 弹出提问/确认表单（支持多个问题一次确认，类似 Claude Code AskUserQuestion），
/// 返回按提问顺序的回答数组；用户取消返回 <c>null</c>。供 <c>ask_user</c> 工具调用。UI 无关接口。
/// </summary>
public interface IAskUserDialogService
{
    Task<string[]?> ShowAsync(IReadOnlyList<AskUserQuestionInput> questions);
}

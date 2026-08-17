using System.Threading.Tasks;

namespace Acme.Desktop.Mvvm;

/// <summary>报告展示弹窗服务（接口 Mvvm，UI-agnostic；impl 在 Shared/Shell 用 ThemedWindow+SelectableMarkdownViewer）。
/// 照 <see cref="IApprovalDialogService"/> 范式：展示一段 markdown + 关闭/复制按钮，无返回值。</summary>
public interface IReportDialogService
{
    /// <summary>展示 markdown 报告弹窗。title 为窗口标题（动态，如"日报 2026-08-17"）。</summary>
    Task ShowAsync(string title, string markdown);
}

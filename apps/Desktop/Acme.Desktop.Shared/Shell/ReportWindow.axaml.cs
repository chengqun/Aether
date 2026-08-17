using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;

namespace Acme.Desktop.Shared.Shell;

/// <summary>报告展示窗口（ThemedWindow + SelectableMarkdownViewer）。标题由 ReportDialogService 动态设置。
/// SelectableMarkdownViewer 自带 Ctrl/Cmd+C 复制选中文本；「复制」按钮复原始 markdown 全文到剪贴板（仿 ChatView.OnCopyMessage）。</summary>
public partial class ReportWindow : ThemedWindow
{
    public ReportWindow() { InitializeComponent(); }

    private async void OnCopyReport(object? sender, RoutedEventArgs e)
    {
        if (DataContext is ReportDialogViewModel vm && !string.IsNullOrEmpty(vm.Markdown))
        {
            var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
            if (clipboard is not null)
                await ClipboardExtensions.SetTextAsync(clipboard, vm.Markdown);
        }
    }
}

using System;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Acme.Desktop.Shared.Shell;

/// <summary>报告展示弹窗 VM（照 ApprovalDialogViewModel：Markdown 只读 + Finished 关窗契约 + CloseCommand）。无返回值。</summary>
public partial class ReportDialogViewModel : ObservableObject
{
    /// <summary>待展示的 markdown 报告文本（只读，由构造注入）。</summary>
    public string Markdown { get; }

    public ReportDialogViewModel(string markdown) => Markdown = markdown ?? string.Empty;

    /// <summary>关闭命令执行完毕触发；Service 订阅以关闭窗口（与 ApprovalDialogViewModel.Finished 同契约）。</summary>
    public event Action? Finished;

    [RelayCommand]
    private void Close() => Finished?.Invoke();
}

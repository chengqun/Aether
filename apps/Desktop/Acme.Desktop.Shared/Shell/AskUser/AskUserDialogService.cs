using Acme.Desktop.Mvvm;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Threading;

namespace Acme.Desktop.Shared.Shell;

/// <summary>
/// <see cref="IAskUserDialogService"/> 的 Avalonia 实现：模态弹出 <see cref="AskUserWindow"/>（多问题表单），
/// 返回按序回答数组。任何线程调用均安全（内部 marshal 到 UI 线程）。
/// </summary>
public sealed class AskUserDialogService : IAskUserDialogService
{
    public async Task<string[]?> ShowAsync(IReadOnlyList<AskUserQuestionInput> questions)
    {
        if (Application.Current?.ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime desktop
            || desktop.MainWindow is null)
        {
            return null;
        }

        return await Dispatcher.UIThread.InvokeAsync(async () =>
        {
            var vm = new AskUserDialogViewModel(questions);
            var window = new AskUserWindow { DataContext = vm };
            vm.Finished += window.Close;
            // 与 ApprovalDialogService 同：后台线程经 InvokeAsync 弹模态窗在 macOS 上可能不激活
            // → 父窗禁用、弹窗不可见 = 假死。显式激活确保可见。
            window.ShowActivated = true;
            window.Opened += (_, _) => window.Activate();
            await window.ShowDialog(desktop.MainWindow);
            return vm.Result;
        });
    }
}

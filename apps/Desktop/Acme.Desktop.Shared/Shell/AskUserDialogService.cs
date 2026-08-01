using Acme.Shell;
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
            await window.ShowDialog(desktop.MainWindow);
            return vm.Result;
        });
    }
}

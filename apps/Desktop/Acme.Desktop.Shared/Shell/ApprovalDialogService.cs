using Acme.Desktop.Mvvm;
using Aether.AI.Core.Agents;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Threading;

namespace Acme.Desktop.Shared.Shell;

/// <summary>
/// <see cref="IApprovalDialogService"/> 的 Avalonia 实现：在主窗口上模态弹出
/// <see cref="ApprovalWindow"/>，返回用户的审批决定。任何线程调用均安全（内部 marshal 到 UI 线程）。
/// </summary>
public sealed class ApprovalDialogService : IApprovalDialogService
{
    public async Task<ToolApprovalDecision?> ShowAsync(ToolApprovalRequest request)
    {
        if (Application.Current?.ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime desktop
            || desktop.MainWindow is null)
        {
            return null;
        }

        return await Dispatcher.UIThread.InvokeAsync(async () =>
        {
            var vm = new ApprovalDialogViewModel(request);
            var window = new ApprovalWindow { DataContext = vm };
            vm.Finished += window.Close;
            await window.ShowDialog(desktop.MainWindow);
            return vm.Result;
        });
    }
}

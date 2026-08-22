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
            // macOS/Avalonia：经后台线程 InvokeAsync 弹出的模态窗有时不会自动激活置顶——
            // 父窗口被 ShowDialog 禁用、弹窗却不可见，整个应用表现为「完全卡死」
            // （WriteFile 等 Confirm 级工具的审批卡就是卡在这里：用户看到工具卡后界面冻住、
            // 却看不到审批框）。显式 ShowActivated + Opened 时 Activate() 确保弹窗置前可见。
            window.ShowActivated = true;
            window.Opened += (_, _) => window.Activate();
            await window.ShowDialog(desktop.MainWindow);
            return vm.Result;
        });
    }
}

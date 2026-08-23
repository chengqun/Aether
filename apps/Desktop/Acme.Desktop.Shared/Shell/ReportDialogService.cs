using System.Threading.Tasks;
using Acme.Desktop.Mvvm;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Threading;

namespace Acme.Desktop.Shared.Shell;

/// <summary>报告展示弹窗服务 impl（照 ApprovalDialogService：取主窗 owner → UI 线程封送 → new ReportWindow + VM → ShowDialog await）。
/// 标题动态（如"日报 2026-08-17"）由调用方传入设到 window.Title。macOS 后台线程模态需 ShowActivated+Opened→Activate（同 AskUser/Approval）。</summary>
public sealed class ReportDialogService : IReportDialogService
{
    public async Task ShowAsync(string title, string markdown)
    {
        if (Application.Current?.ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime desktop
            || desktop.MainWindow is null) return;

        await Dispatcher.UIThread.InvokeAsync(async () =>
        {
            var vm = new ReportDialogViewModel(markdown);
            var window = new ReportWindow { Title = title, DataContext = vm };
            vm.Finished += window.Close;
            window.ShowActivated = true;
            window.Opened += (_, _) => window.Activate();
            await DesktopApp.ShowModalWithVisibleOwnerAsync(window);
        });
    }
}

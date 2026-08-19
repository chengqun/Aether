using System.Threading;
using System.Threading.Tasks;
using Acme.Desktop.Mvvm;
using Acme.Desktop.Mvvm.Palette;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Threading;

namespace Acme.Desktop.Shared.Shell;

/// <summary>调色板弹窗服务 impl（照 ReportDialogService：取主窗 owner → UI 线程封送 → new PaletteWindow + VM → ShowDialog await）。
/// 从 host 当前主题派生可调色；实时预览/保存/取消由 VM 经 host 驱动。macOS 后台线程模态需 ShowActivated+Opened→Activate（同 Report/Approval）。</summary>
public sealed class PaletteDialogService : IPaletteDialogService
{
    public async Task ShowAsync(IThemeHost host, CancellationToken cancellationToken = default)
    {
        if (Application.Current?.ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime desktop
            || desktop.MainWindow is null) return;

        await Dispatcher.UIThread.InvokeAsync(async () =>
        {
            var vm = new PaletteViewModel(host);
            var window = new PaletteWindow { Title = "调色板", DataContext = vm };
            vm.Finished += window.Close;
            window.ShowActivated = true;
            window.Opened += (_, _) => window.Activate();
            await window.ShowDialog(desktop.MainWindow);
        });
    }
}

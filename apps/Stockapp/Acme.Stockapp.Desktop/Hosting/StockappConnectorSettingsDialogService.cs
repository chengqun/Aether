using Acme.Shell;
using Acme.Stockapp.Desktop.ViewModels;
using Acme.Stockapp.Desktop.Views;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Threading;

namespace Acme.Stockapp.Desktop.Hosting;

/// <summary>
/// <see cref="IConnectorSettingsDialogService"/> 的 Stockapp 实现：按卡片 VM 类型弹出对应设置窗口
/// （CTP→<see cref="CtpLoginWindow"/>，同花顺→<see cref="ThsSettingsWindow"/>），主窗口为父，模态。
/// 任何线程调用均安全（内部 marshal 到 UI 线程）。平行于 <c>ApprovalDialogService</c>。
/// </summary>
public sealed class StockappConnectorSettingsDialogService : IConnectorSettingsDialogService
{
    public async Task ShowSettingsAsync(ConnectorCardViewModel card)
    {
        if (Application.Current?.ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime desktop
            || desktop.MainWindow is null)
        {
            return;
        }

        await Dispatcher.UIThread.InvokeAsync(async () =>
        {
            Window? window = card switch
            {
                CtpConnectorViewModel => new CtpLoginWindow { DataContext = card },
                ThsConnectorViewModel => new ThsSettingsWindow { DataContext = card },
                _ => null,
            };
            if (window is not null)
                await window.ShowDialog(desktop.MainWindow);
        });
    }
}

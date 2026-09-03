using Acme.Desktop.Mvvm;
using Acme.Qifu.Desktop.ViewModels;
using Acme.Qifu.Desktop.Views;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Threading;

namespace Acme.Qifu.Desktop.Hosting;

/// <summary>
/// <see cref="IConnectorSettingsDialogService"/> 的 Qifu 实现：按卡片 VM 类型弹出对应设置窗口
/// （MySQL→<see cref="MySqlSettingsWindow"/>），主窗口为父，模态。任何线程调用均安全
/// （内部 marshal 到 UI 线程）。平行于 Stockapp 的 <c>StockappConnectorSettingsDialogService</c>。
/// Qifu 仅 MySQL 卡用设置窗；毓数/mcphub/云文档三张直接登录卡不经过此服务。
/// </summary>
public sealed class QifuConnectorSettingsDialogService : IConnectorSettingsDialogService
{
    public async Task ShowSettingsAsync(ConnectorCardViewModel card)
    {
        if (Avalonia.Application.Current?.ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime desktop
            || desktop.MainWindow is null)
        {
            return;
        }

        await Dispatcher.UIThread.InvokeAsync(async () =>
        {
            Window? window = card switch
            {
                MySqlConnectorCardViewModel => new MySqlSettingsWindow { DataContext = card },
                _ => null,
            };
            if (window is not null)
                await window.ShowDialog(desktop.MainWindow);
        });
    }
}

using System.Threading.Tasks;
using Acme.Desktop.Mvvm.Git;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Threading;

namespace Acme.Desktop.Shared.Shell;

/// <summary><see cref="IRemoteSettingsDialogService"/> 的实现：打开 <see cref="RemoteSettingsWindow"/>
/// （模态，主窗口为父）。窗口内自管表单/持久化（经 DesktopApp.Services）。任何线程调用均安全。</summary>
public sealed class RemoteSettingsDialogService : IRemoteSettingsDialogService
{
    public async Task ShowAsync()
    {
        if (Application.Current?.ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime desktop
            || desktop.MainWindow is null)
        {
            return;
        }
        await Dispatcher.UIThread.InvokeAsync(async () =>
        {
            var window = new RemoteSettingsWindow();
            await DesktopApp.ShowModalWithVisibleOwnerAsync(window);
        });
    }
}

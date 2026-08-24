using System.Threading.Tasks;
using Acme.Desktop.Mvvm.Git;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Threading;

namespace Acme.Desktop.Shared.Shell;

/// <summary><see cref="IRemoteSettingsDialogService"/> 的实现：打开 <see cref="RemoteSettingsWindow"/>
/// （模态，主窗口为父）。窗口内自管表单/持久化（经 DesktopApp.Services）。任何线程调用均安全。
/// <paramref name="repoPath"/> 透传给窗口 ctor——窗口据此（而非硬解析目录 provider）操作目标仓库，
/// 修「从知识对象 SCM 打开会误探项目目录」的旧 bug。</summary>
public sealed class RemoteSettingsDialogService : IRemoteSettingsDialogService
{
    public async Task ShowAsync(string? repoPath)
    {
        if (Application.Current?.ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime desktop
            || desktop.MainWindow is null)
        {
            return;
        }
        await Dispatcher.UIThread.InvokeAsync(async () =>
        {
            var window = new RemoteSettingsWindow(repoPath);
            await DesktopApp.ShowModalWithVisibleOwnerAsync(window);
        });
    }
}

using Acme.Desktop.Mvvm;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Microsoft.Extensions.DependencyInjection;

namespace Acme.Desktop.Shared.Shell;

/// <summary>
/// <see cref="ISettingsDialogService"/> 的 Avalonia 实现：从 Application.Current 取主窗口，
/// 把 SettingsWindow 作为模态对话框弹出。供两租户共用。
/// </summary>
public sealed class SettingsDialogService : ISettingsDialogService
{
    private readonly IServiceProvider _serviceProvider;

    public SettingsDialogService(IServiceProvider serviceProvider)
        => _serviceProvider = serviceProvider;

    public async Task ShowSettingsAsync()
    {
        if (Application.Current?.ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime desktop
            || desktop.MainWindow is null)
        {
            return;
        }

        var vm = _serviceProvider.GetRequiredService<SettingsViewModel>();
        await vm.LoadAsync();

        vm.SettingsSaved += () =>
        {
            RefreshMainViewModelConnectionState();
        };

        var window = new SettingsWindow { DataContext = vm };
        await window.ShowDialog(desktop.MainWindow);

        RefreshMainViewModelConnectionState();
    }

    private void RefreshMainViewModelConnectionState()
    {
        if (Application.Current?.ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime desktop
            || desktop.MainWindow is null)
        {
            return;
        }

        if (desktop.MainWindow.DataContext is MainViewModelBase vm)
        {
            vm.RefreshConnectionState();
        }
    }
}

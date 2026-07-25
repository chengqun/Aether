using Microsoft.Extensions.DependencyInjection;

namespace Acme.Desktop.Shared.Shell;

/// <summary>弹出设置对话框。</summary>
public interface ISettingsDialogService
{
    Task ShowSettingsAsync();
}

/// <summary>
/// 默认实现：从 Application.Current.ApplicationLifetime 取主窗口，
/// 把 SettingsWindow 作为模态对话框弹出。供两租户共用。
/// </summary>
public sealed class SettingsDialogService : ISettingsDialogService
{
    private readonly IServiceProvider _serviceProvider;

    public SettingsDialogService(IServiceProvider serviceProvider)
        => _serviceProvider = serviceProvider;

    public async Task ShowSettingsAsync()
    {
        if (Avalonia.Application.Current?.ApplicationLifetime is not Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime desktop
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
        if (Avalonia.Application.Current?.ApplicationLifetime is not Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime desktop
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
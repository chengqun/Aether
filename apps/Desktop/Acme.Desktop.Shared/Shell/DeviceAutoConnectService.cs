using Acme.Desktop.Mvvm;
using Microsoft.Extensions.DependencyInjection;

namespace Acme.Desktop.Shared.Shell;

/// <summary>
/// <see cref="IDeviceAutoConnectService"/> 的桌面实现：委托给 <see cref="SettingsViewModel"/>
/// 的加载 + 自动连接逻辑。使 UI 无关的 MainViewModelBase 不直接依赖 SettingsViewModel。
/// </summary>
public sealed class DeviceAutoConnectService : IDeviceAutoConnectService
{
    private readonly IServiceProvider _serviceProvider;

    public DeviceAutoConnectService(IServiceProvider serviceProvider)
        => _serviceProvider = serviceProvider;

    public async Task LoadAndAutoConnectAsync()
    {
        var vm = _serviceProvider.GetRequiredService<SettingsViewModel>();
        await vm.LoadAsync();
        await vm.AutoConnectIfEnabledAsync();
    }
}

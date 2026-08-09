namespace Acme.Desktop.Mvvm;

/// <summary>
/// 启动期设备自动连接服务（加载设备配置并按需连接）。UI 无关接口；
/// 实现由宿主提供（桌面端委托给 SettingsViewModel 的逻辑）。
/// </summary>
public interface IDeviceAutoConnectService
{
    /// <summary>加载设备配置；若启用自动连接则连接。</summary>
    Task LoadAndAutoConnectAsync();
}

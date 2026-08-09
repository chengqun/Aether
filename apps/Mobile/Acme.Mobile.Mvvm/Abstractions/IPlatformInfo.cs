using Acme.AppUpdate.Domain;

namespace Acme.Mobile.Mvvm.Abstractions;

/// <summary>
/// 平台信息抽象，替代 MAUI 的 DeviceInfo/AppInfo
/// 平台层注册具体实现
/// </summary>
public interface IPlatformInfo
{
    /// <summary>
    /// 获取当前应用平台
    /// </summary>
    AppPlatform GetAppPlatform();

    /// <summary>
    /// 获取当前应用版本号（整数）
    /// </summary>
    int GetVersionCode();

    /// <summary>
    /// 获取当前应用版本字符串（如 "1.0.5"）
    /// </summary>
    string GetVersionString();
}

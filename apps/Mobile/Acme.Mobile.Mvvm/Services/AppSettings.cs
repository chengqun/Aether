namespace Acme.Mobile.Mvvm.Services;

/// <summary>
/// 应用配置抽象，替代 MAUI/Avalonia 的静态 AppSettings
/// 平台层注册具体实现
/// </summary>
public interface IAppSettings
{
    /// <summary>
    /// 后端服务器地址
    /// </summary>
    string ServerBaseAddress { get; }

    /// <summary>
    /// 获取字符串类型的配置值（替代 MAUI Preferences.Default.Get）
    /// </summary>
    string GetString(string key, string defaultValue);

    /// <summary>
    /// 设置字符串类型的配置值（替代 MAUI Preferences.Default.Set）
    /// </summary>
    void SetString(string key, string value);
}

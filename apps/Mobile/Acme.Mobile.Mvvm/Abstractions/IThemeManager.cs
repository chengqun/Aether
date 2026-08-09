using System;
using System.Threading.Tasks;

namespace Acme.Mobile.Mvvm.Abstractions;

/// <summary>
/// 主题模式
/// </summary>
public enum ThemeMode
{
    System,
    Light,
    Dark
}

/// <summary>
/// 主题管理抽象，替代 MAUI/Avalonia 的平台特定 ThemeManager
/// </summary>
public interface IThemeManager
{
    ThemeMode CurrentTheme { get; }
    event EventHandler<ThemeMode>? ThemeChanged;
    void Initialize();
    Task SetThemeAsync(ThemeMode themeMode);
    Task<ThemeMode> GetSavedThemeAsync();
}
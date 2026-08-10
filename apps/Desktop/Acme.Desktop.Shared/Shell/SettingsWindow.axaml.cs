using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using Microsoft.Extensions.DependencyInjection;

namespace Acme.Desktop.Shared.Shell;

public partial class SettingsWindow : Window
{
    private IThemeApplier? _themeApplier;

    public SettingsWindow()
    {
        InitializeComponent();
        // 子窗口不在 Workbench 层、拿不到 ThemeLoader——经 IThemeApplier 桥接叠加 overlay，
        // 否则 dark 下设置窗的自定义 token 区（卡片/边框）仍是 SharedTheme 浅色基线（白块）。
        _themeApplier = DesktopApp.Services.GetService<IThemeApplier>();
        if (_themeApplier is not null)
            _themeApplier.ThemeChanged += OnThemeChanged;
    }

    private void InitializeComponent()
    {
        AvaloniaXamlLoader.Load(this);
    }

    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
        _themeApplier?.ApplyTheme(this);
    }

    private void OnThemeChanged(object? sender, EventArgs e) => _themeApplier?.ApplyTheme(this);

    protected override void OnClosed(EventArgs e)
    {
        if (_themeApplier is not null)
            _themeApplier.ThemeChanged -= OnThemeChanged;
        base.OnClosed(e);
    }
}

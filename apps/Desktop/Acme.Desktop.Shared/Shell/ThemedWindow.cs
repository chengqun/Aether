using System;
using Avalonia.Controls;
using Microsoft.Extensions.DependencyInjection;

namespace Acme.Desktop.Shared.Shell;

/// <summary>
/// 主题感知窗口基类：自动经 <see cref="IThemeApplier"/> 叠加当前主题 overlay，并订阅主题变化即时重染。
/// <para>
/// 子窗口（设置/Trace/各编辑器/登录…）在 Shared 层拿不到 Workbench 的 ThemeLoader——不经此基类，
/// dark 主题下它们的自定义 token 区（<c>Background={DynamicResource CardBgBrush}</c> 等）仍取 SharedTheme
/// 浅色基线（#FFFFFF/#F7F7F8），而原生控件已随 ThemeVariant 翻深 → 深色控件落在白底上（白块）。
/// </para>
/// <para>
/// 原生控件 ThemeVariant 已由 WorkbenchWindow 设到 Application 级（全窗继承），此基类只管 overlay 画刷。
/// 设计期（<see cref="DesktopApp.Services"/> 未就绪）→ _themeApplier 为 null，静默跳过，不影响设计器渲染。
/// </para>
/// </summary>
public abstract class ThemedWindow : Window
{
    private IThemeApplier? _themeApplier;

    protected ThemedWindow()
    {
        _themeApplier = DesktopApp.Services.GetService<IThemeApplier>();
        if (_themeApplier is not null)
            _themeApplier.ThemeChanged += OnThemeChanged;
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

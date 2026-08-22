using System;
using Acme.Desktop.Mvvm;
using Acme.Desktop.Shared.Theming;
using Avalonia.Controls;
using Microsoft.Extensions.DependencyInjection;

namespace Acme.Desktop.Shared.Shell;

/// <summary>
/// 主题感知窗口基类：自动经 <see cref="ThemeLoader"/> 叠加当前主题 overlay，并订阅主题变化即时重染。
/// <para>
/// 主题引擎（ThemeLoader/ThemeDefinition）已下沉到 Shared 层，此基类直接调用 ThemeLoader（读 <see cref="IThemeHost"/>
/// 取当前 overlay JSON），无需经 Workbench 桥接。子窗口（设置/Trace/各编辑器/登录…）不经此基类，dark 主题下它们的
/// 自定义 token 区（<c>Background={DynamicResource CardBgBrush}</c> 等）仍取 SharedTheme 浅色基线（#FFFFFF/#F7F7F8），
/// 而原生控件已随 ThemeVariant 翻深 → 深色控件落在白底上（白块）。
/// </para>
/// <para>
/// 原生控件 ThemeVariant 已由 WorkbenchWindow 设到 Application 级（全窗继承），此基类只管 overlay 画刷。
/// 设计期（<see cref="DesktopApp.Services"/> 未就绪）→ _host 为 null，静默跳过，不影响设计器渲染。
/// </para>
/// </summary>
public abstract class ThemedWindow : Window
{
    private IThemeHost? _host;

    protected ThemedWindow()
    {
        _host = DesktopApp.Services.GetService<MainViewModelBase>() as IThemeHost;
        if (_host is not null)
            _host.ThemeChanged += OnThemeChanged;
    }

    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
        ApplyTheme();
    }

    private void ApplyTheme() => ThemeLoader.Apply(this, _host?.CustomThemeJson);

    private void OnThemeChanged(object? sender, EventArgs e) => ApplyTheme();

    protected override void OnClosed(EventArgs e)
    {
        if (_host is not null)
            _host.ThemeChanged -= OnThemeChanged;
        base.OnClosed(e);
    }
}

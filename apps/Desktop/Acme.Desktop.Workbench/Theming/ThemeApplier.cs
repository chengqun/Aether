using System;
using Acme.Desktop.Mvvm;
using Acme.Desktop.Shared.Shell;
using Avalonia;

namespace Acme.Desktop.Workbench.Theming;

/// <summary>
/// <see cref="IThemeApplier"/> 实现：桥接子窗口（设置/Trace，Shared 层）到 Workbench 的 <see cref="ThemeLoader"/>。
/// 从 <see cref="IThemeHost"/>（shell VM）读当前 overlay JSON + 转发 <see cref="IThemeHost.ThemeChanged"/>。
/// 原生控件 ThemeVariant 已由 WorkbenchWindow 设到 Application 级（子窗口继承），此处只叠加 overlay 画刷。
/// </summary>
public sealed class ThemeApplier : IThemeApplier
{
    private readonly IThemeHost? _host;

    public ThemeApplier(MainViewModelBase shell) => _host = shell as IThemeHost;

    /// <inheritdoc/>
    public void ApplyTheme(StyledElement target)
    {
        if (_host is { } h)
            ThemeLoader.Apply(target, h.CustomThemeJson);
    }

    /// <inheritdoc/>
    public event EventHandler? ThemeChanged
    {
        add { if (_host is { } h) h.ThemeChanged += value; }
        remove { if (_host is { } h) h.ThemeChanged -= value; }
    }
}

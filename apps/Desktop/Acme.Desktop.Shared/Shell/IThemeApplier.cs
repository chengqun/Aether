using System;
using Avalonia;

namespace Acme.Desktop.Shared.Shell;

/// <summary>
/// 主题应用器：把当前主题 overlay 叠加到目标窗口的 Resources。子窗口（设置/Trace）在 Shared 层、
/// 拿不到 Workbench 的 <c>ThemeLoader</c>，经此接口桥接（依赖倒置：接口在 Shared，实现在 Workbench）。
/// 子窗口 <c>OnOpened</c> 调 <see cref="ApplyTheme"/>，并订阅 <see cref="ThemeChanged"/> 即时重染。
/// 原生控件 <c>ThemeVariant</c> 已由 WorkbenchWindow 设到 Application 级（全窗继承），此接口只管 overlay 画刷。
/// </summary>
public interface IThemeApplier
{
    /// <summary>把当前主题 overlay 叠加到 target 的 Resources.MergedDictionaries（重复调先移除旧 overlay 再加新）。</summary>
    void ApplyTheme(StyledElement target);

    /// <summary>当前主题变化时触发（订阅者应重新 <see cref="ApplyTheme"/> 重染）。</summary>
    event EventHandler? ThemeChanged;
}

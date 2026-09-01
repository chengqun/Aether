using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Styling;
using Markdown.Avalonia.Utils;

namespace Acme.Desktop.Shared.Shell.Controls;

/// <summary>
/// mermaid 容器块处理器：实现 Markdown.Avalonia 的 <see cref="IContainerBlockHandler"/> 扩展点，
/// 拦截 <c>::: mermaid</c> 自定义容器块，把内容经 <see cref="MermaidSvgRenderer"/> 渲染成矢量图（Svg 控件直显，免光栅）。
/// 标准 <c>```mermaid</c> fence 由 <see cref="SelectableMarkdownViewer"/> 在喂引擎前预处理转成
/// <c>::: mermaid</c> 容器，故两种语法都能渲染（LLM/复制的标准 mermaid 无需用户改写）。
/// 非 mermaid 容器（<c>::: note</c> 等）返回 null → Markdown.Avalonia 回退默认渲染（实测安全）。
/// 主题感知：从 owner 的 <see cref="Avalonia.StyledElement.ActualThemeVariant"/> 读 dark 标志选配色；
/// 主题切换后由 owner 重渲染 markdown 触发 ProvideControl 再调，配色随主题刷新。
/// </summary>
internal sealed class MermaidBlockHandler : IContainerBlockHandler
{
    private readonly SelectableMarkdownViewer _owner;

    public MermaidBlockHandler(SelectableMarkdownViewer owner) => _owner = owner;

    public Border? ProvideControl(string? assetPathRoot, string? blockName, string? lines)
    {
        if (string.IsNullOrWhiteSpace(blockName)
            || !blockName.Contains("mermaid", StringComparison.OrdinalIgnoreCase))
            return null;

        var dark = _owner.ActualThemeVariant == ThemeVariant.Dark;
        var img = MermaidSvgRenderer.Render(lines ?? string.Empty, dark);
        if (img is null)
            return null; // 语法错/不支持 → 回退默认代码块（用户看到源码，不崩）

        return new Border
        {
            Child = img,
            Padding = new Thickness(0),
            Margin = new Thickness(0, 4),
        };
    }
}

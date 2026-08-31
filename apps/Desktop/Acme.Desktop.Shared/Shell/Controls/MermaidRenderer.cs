using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Svg.Skia;
using Markdown.Avalonia;
using Mermaider.Models;

namespace Acme.Desktop.Shared.Shell.Controls;

/// <summary>
/// mermaid 图渲染器：把 mermaid 源码经 Mermaider（纯 .NET，无浏览器/JS）渲染成 SVG，
/// 再经 Avalonia.Svg.Skia 转成 <see cref="Image"/>（Source=<see cref="SvgImage"/>）显示。
/// 经 Markdown.Avalonia 的 IContainerBlockHandler 扩展点注入：拦截 <c>::: mermaid</c> 容器块，
/// 不换 markdown 引擎、不改 markdown 文本（标准 ```mermaid 由 SelectableMarkdownViewer 预处理转 <c>::: mermaid</c>）。
/// 主题感知：按 ActualThemeVariant 选 light/dark 配色，切换后由调用方重渲染刷新。
/// </summary>
internal static class MermaidSvgRenderer
{
    /// <summary>把 mermaid 源码渲染成 Avalonia Image 控件（矢量 SVG，不经光栅位图）。
    /// 失败返回 null（调用方回退默认代码块渲染，不崩）。</summary>
    public static Image? Render(string mermaidSource, bool dark)
    {
        try
        {
            var svg = Mermaider.MermaidRenderer.RenderSvg(mermaidSource, BuildOptions(dark));
            if (string.IsNullOrWhiteSpace(svg)) return null;
            return SvgToImage(svg);
        }
        catch
        {
            // mermaid 语法错 / Mermaider 不支持的图型 → 回退默认代码块（不崩，用户看到源码）。
            return null;
        }
    }

    /// <summary>当前主题的 RenderOptions：light/dark 配色。其余走 Mermaider 默认。</summary>
    private static RenderOptions BuildOptions(bool dark) => new()
    {
        Bg = dark ? "#1e1e1e" : "#ffffff",
        Fg = dark ? "#d4d4d4" : "#1e1e1e",
        Accent = dark ? "#4fc3f7" : "#1565C0",
    };

    /// <summary>SVG 字符串 → Avalonia Image（经 SvgSource → SvgImage，矢量渲染）。Mermaider 输出已 sanitize。</summary>
    private static Image? SvgToImage(string svg)
    {
        var source = SvgSource.LoadFromSvg<SvgSource>(svg);
        if (source is null) return null;
        return new Image
        {
            Source = new SvgImage { Source = source },
            HorizontalAlignment = HorizontalAlignment.Left,
            Margin = new Thickness(0, 4),
        };
    }
}

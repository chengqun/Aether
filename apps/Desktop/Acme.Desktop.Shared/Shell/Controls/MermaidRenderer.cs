using System;
using System.IO;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media.Imaging;
using Mermaider.Models;
using SkiaSharp;
using Svg.Skia;

namespace Acme.Desktop.Shared.Shell.Controls;

/// <summary>
/// mermaid 图渲染器：把 mermaid 源码经 Mermaider（纯 .NET，无浏览器/JS，输出 SVG 字符串）渲染，
/// 再经 Svg.Skia 的 SKSvg 解析成 SKPicture，用主 SkiaSharp 画到 SKBitmap → 编码 PNG → Avalonia Bitmap 显示。
/// 经 Markdown.Avalonia 的 IContainerBlockHandler 扩展点注入：拦截 <c>::: mermaid</c> 容器块，
/// 不换 markdown 引擎、不改 markdown 文本（标准 ```mermaid 由 SelectableMarkdownViewer 预处理转 <c>::: mermaid</c>）。
/// 主题感知：按 ActualThemeVariant 选 light/dark 配色，切换后由调用方重渲染刷新。
/// </summary>
/// <remarks>
/// 不用 Avalonia.Svg.Skia 11.0.0.12（SvgImage/SvgSource 矢量控件路径）：它绑 Svg.Skia 1.0.0.12，
/// 后者编译期绑 SkiaSharp 2.88.x 的 CropRect 类型（已移到 ShimSkiaSharp），在 SkiaSharp 3.119.4 运行时
/// TypeLoadException「Could not load type 'CropRect'」。Svg.Skia 3.7.0 重写后无 CropRect 引用，
/// SkiaSharp 2.88.x 核心 API 在 3.119.4 稳定。光栅位图（PNG）而非矢量——mermaid 图为静态渲染，位图够用且零 SvgImage 控件坑。
/// </remarks>
internal static class MermaidSvgRenderer
{
    /// <summary>把 mermaid 源码渲染成 Avalonia Image 控件。失败返回 null（调用方回退默认代码块渲染，不崩）。</summary>
    public static Image? Render(string mermaidSource, bool dark)
    {
        try
        {
            var svg = Mermaider.MermaidRenderer.RenderSvg(mermaidSource, BuildOptions(dark));
            if (string.IsNullOrWhiteSpace(svg)) return null;
            return SvgToImage(svg, dark);
        }
        catch
        {
            // 渲染失败（语法错/不支持图型/Svg.Skia 与 SkiaSharp 3.119.4 的其它 API 差异）→ null，
            // ProvideControl 回退默认代码块（用户看到源码，不崩）。
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

    /// <summary>SVG 字符串 → Avalonia Image。经 SKSvg.FromSvg 解析成 SKPicture，画到 SKBitmap（光栅化），
    /// 编码 PNG 再包成 Avalonia Bitmap（Image.Source）。用主 SkiaSharp 3.119.4，与桌宠 Lottie 同一渲染栈。</summary>
    private static Image? SvgToImage(string svg, bool dark)
    {
        using var sk = new SKSvg();
        var picture = sk.FromSvg(svg);
        if (picture is null) return null;

        try
        {
            // CullRect 是 SVG 的内容包围盒（含负原点时需平移让内容落进位图）。
            var bounds = picture.CullRect;
            var w = (int)Math.Ceiling(bounds.Width);
            var h = (int)Math.Ceiling(bounds.Height);
            if (w <= 0 || h <= 0) return null;

            using var bitmap = new SKBitmap(w, h);
            using (var canvas = new SKCanvas(bitmap))
            {
                canvas.Clear(dark ? new SKColor(0x1e, 0x1e, 0x1e) : SKColors.White);
                // 负原点 SVG（内容向左/上延伸）平移回位图原点。
                if (bounds.Left != 0 || bounds.Top != 0)
                    canvas.Translate((float)-bounds.Left, (float)-bounds.Top);
                canvas.DrawPicture(picture);
            }

            using var png = bitmap.Encode(SKEncodedImageFormat.Png, 100);
            using var ms = new MemoryStream();
            png.SaveTo(ms);
            ms.Position = 0;

            return new Image
            {
                Source = new Bitmap(ms),
                HorizontalAlignment = HorizontalAlignment.Left,
                Margin = new Thickness(0, 4),
            };
        }
        finally
        {
            picture.Dispose();
        }
    }
}

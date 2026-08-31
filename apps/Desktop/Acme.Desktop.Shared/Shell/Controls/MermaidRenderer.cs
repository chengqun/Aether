using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
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
            // Mermaider SVG 用 CSS 自定义属性（var(--_xxx)）+ color-mix() 表达所有形状填色，
            // Svg.Skia 的 CSS 引擎不解析 var()/color-mix() → fill="var(--_node-fill)" 无效色 → 黑块。
            // 喂 SKSvg 前展平成具体色。失败兜底用原 SVG（最坏回退黑块→ProvideControl 返 null→默认代码块）。
            try { svg = FlattenCssVars(svg); }
            catch { /* 展平失败不阻断，SvgToImage 自会兜底 */ }
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

    /// <summary>
    /// 把 Mermaider SVG 的 CSS 自定义属性（<c>var(--x)</c>）+ <c>color-mix(in srgb, A P%, B)</c>
    /// 展平成 Svg.Skia 能解析的具体颜色/字号。Mermaider 给所有形状填色都走 CSS 变量派生
    /// （<c>fill="var(--_node-fill)"</c>，<c>--_node-fill: color-mix(in srgb, var(--fg) 10%, var(--bg))</c>），
    /// Svg.Skia 不解析 <c>var()</c>/<c>color-mix()</c> → 无效填色 → 黑块。本方法解析变量依赖图求值后
    /// 全文替换 <c>var()</c> 引用为具体值。图型无关（对任何 Mermaider 输出通用）。
    /// </summary>
    /// <remarks>
    /// 三类输入：(1) 基础变量在 <c>&lt;svg style="--bg:..;--fg:..;--accent:.."/&gt;</c> 内联；
    /// (2) 派生变量 <c>--_xxx: var(--base, color-mix(...))</c> 在 <c>&lt;style&gt;</c> 块的 <c>svg {...}</c> 规则里；
    /// (3) 形状内联属性 <c>fill="var(--_node-fill)"</c> / <c>font-size="var(--fs-m)"</c>。解析顺序：
    /// 收集基础+派生变量 → 递归求值每个变量（处理 var() 引用、color-mix() 混色、rem→px）→
    /// 全文 regex 替换 <c>var(--xxx)</c> 为具体值。<c>var(--base, fallback)</c> 的 fallback 仅当 base 未定义时取。
    /// 残留在 <c>&lt;style&gt;</c> 块定义里的 <c>var()</c>（如 <c>--_line: var(--line, color-mix(...))</c>）
    /// 无害——形状内联属性已是具体色，Svg.Skia 直读内联属性不靠 style 块变量解析。
    /// </remarks>
    private static string FlattenCssVars(string svg)
    {
        var allVars = new Dictionary<string, string>(StringComparer.Ordinal);
        // (1) 基础变量：<svg style="--bg:#fff;--fg:#1e1e1e;--accent:#1565C0">
        var svgStyle = Regex.Match(svg, @"<svg[^>]*\bstyle=""([^""]*)""");
        if (svgStyle.Success)
            ParseDecls(svgStyle.Groups[1].Value, allVars);
        // (2) 派生变量：<style> 块里 svg { --_node-fill: color-mix(...); }
        var styleBlock = Regex.Match(svg, @"<style[^>]*>([\s\S]*?)</style>");
        if (styleBlock.Success)
        {
            var svgRule = Regex.Match(styleBlock.Groups[1].Value, @"\bsvg\s*\{([^{}]*)\}", RegexOptions.Singleline);
            if (svgRule.Success)
                ParseDecls(svgRule.Groups[1].Value, allVars);
        }

        var resolved = new Dictionary<string, string>(StringComparer.Ordinal);
        var resolving = new HashSet<string>(); // 环检测（理论不该有环，防御）

        string ResolveVar(string name)
        {
            if (resolved.TryGetValue(name, out var v)) return v;
            if (!allVars.TryGetValue(name, out _)) return string.Empty; // 未定义
            if (resolving.Contains(name)) return string.Empty; // 环
            resolving.Add(name);
            var r = ResolveExpr(allVars[name]);
            resolving.Remove(name);
            resolved[name] = r;
            return r;
        }

        string ResolveExpr(string expr)
        {
            var e = expr;
            var changed = true;
            while (changed)
            {
                changed = false;
                // 最内层 var(--x) 或 var(--x, fallback)（fallback 不含嵌套括号——先解析嵌套的）
                var mv = Regex.Match(e, @"var\(\s*--([\w-]+)\s*(?:,\s*([^()]*))?\)");
                if (mv.Success)
                {
                    var name = mv.Groups[1].Value;
                    var val = ResolveVar(name);
                    if (string.IsNullOrEmpty(val) && mv.Groups[2].Success)
                        val = mv.Groups[2].Value.Trim(); // base 未定义 → 取 fallback（下一轮再解析）
                    e = e.Remove(mv.Index, mv.Length).Insert(mv.Index, val);
                    changed = true;
                    continue;
                }
                // 最内层 color-mix(in srgb, <色> <pct>%, <色>)（此时两色应已是 #hex）
                var mc = Regex.Match(e,
                    @"color-mix\(\s*in\s+srgb\s*,\s*(#[0-9a-fA-F]{3,8}|rgba?\([^)]*\))\s+(\d+(?:\.\d+)?)%\s*,\s*(#[0-9a-fA-F]{3,8}|rgba?\([^)]*\))\s*\)");
                if (mc.Success)
                {
                    var a = ParseColor(mc.Groups[1].Value);
                    var pct = double.Parse(mc.Groups[2].Value, CultureInfo.InvariantCulture) / 100.0;
                    var b = ParseColor(mc.Groups[3].Value);
                    e = e.Remove(mc.Index, mc.Length).Insert(mc.Index, MixColor(a, pct, b));
                    changed = true;
                    continue;
                }
                // rem → px（Mermaider 字号变量用 rem，Svg.Skia 不认 rem；1rem=16px）
                var mr = Regex.Match(e, @"(\d+\.?\d*)rem");
                if (mr.Success)
                {
                    var px = (double.Parse(mr.Groups[1].Value, CultureInfo.InvariantCulture) * 16)
                        .ToString("0.#", CultureInfo.InvariantCulture) + "px";
                    e = e.Remove(mr.Index, mr.Length).Insert(mr.Index, px);
                    changed = true;
                    continue;
                }
            }
            return e.Trim();
        }

        // 预求值全部变量（含依赖链递归），再全文替换 var(--xxx) 引用。
        foreach (var key in allVars.Keys.ToList())
            ResolveVar(key);

        return Regex.Replace(svg, @"var\(\s*--([\w-]+)\s*(?:,[^()]*)?\)", m =>
        {
            var name = m.Groups[1].Value;
            return resolved.TryGetValue(name, out var v) && !string.IsNullOrEmpty(v) ? v : m.Value;
        });
    }

    /// <summary>把 CSS 声明串（<c>--key: value;</c>，value 可含括号内分号）按括号深度切分后塞进字典。</summary>
    private static void ParseDecls(string decls, Dictionary<string, string> sink)
    {
        foreach (var decl in SplitDecls(decls))
        {
            var i = decl.IndexOf(':');
            if (i < 0) continue;
            var key = decl.Substring(0, i).Trim();
            var val = decl.Substring(i + 1).Trim();
            if (key.StartsWith("--"))
                sink[key.Substring(2)] = val; // 去掉 "--" 前缀，存裸名
        }
    }

    /// <summary>按括号深度切分 CSS 声明（<c>color-mix(...)</c> 内的分号不算分隔）。</summary>
    private static List<string> SplitDecls(string s)
    {
        var result = new List<string>();
        var depth = 0;
        var last = 0;
        for (var i = 0; i < s.Length; i++)
        {
            var c = s[i];
            if (c == '(') depth++;
            else if (c == ')') depth--;
            else if (c == ';' && depth == 0)
            {
                result.Add(s.Substring(last, i - last));
                last = i + 1;
            }
        }
        if (last < s.Length)
            result.Add(s.Substring(last));
        return result.Where(x => x.Trim().Length > 0).ToList();
    }

    /// <summary>解析 CSS 颜色（<c>#hex</c> 3/6/8 位 或 <c>rgb()/rgba()</c>）为 RGBA 元组。</summary>
    private static (byte R, byte G, byte B, byte A) ParseColor(string s)
    {
        s = s.Trim();
        if (s.StartsWith("#"))
        {
            var h = s.Substring(1);
            if (h.Length == 3)
                h = "" + h[0] + h[0] + h[1] + h[1] + h[2] + h[2];
            if (h.Length == 6)
                return (Convert.ToByte(h.Substring(0, 2), 16),
                        Convert.ToByte(h.Substring(2, 2), 16),
                        Convert.ToByte(h.Substring(4, 2), 16),
                        (byte)255);
            if (h.Length == 8)
                return (Convert.ToByte(h.Substring(0, 2), 16),
                        Convert.ToByte(h.Substring(2, 2), 16),
                        Convert.ToByte(h.Substring(4, 2), 16),
                        Convert.ToByte(h.Substring(6, 2), 16));
        }

        if (s.StartsWith("rgb", StringComparison.OrdinalIgnoreCase))
        {
            var inner = s.Substring(s.IndexOf('(') + 1).TrimEnd(')');
            var parts = inner.Split(',');
            byte a = 255;
            if (parts.Length >= 4)
                a = (byte)(double.Parse(parts[3].Trim(), CultureInfo.InvariantCulture) * 255);
            return (byte.Parse(parts[0].Trim()),
                    byte.Parse(parts[1].Trim()),
                    byte.Parse(parts[2].Trim()),
                    a);
        }

        return (0, 0, 0, 255); // 不可解析 → 黑（不应发生，Mermaider 输出标准 #hex）
    }

    /// <summary>CSS <c>color-mix(in srgb, A P%, B)</c>：线性混合 A*P% + B*(1-P%)，alpha 取 255。</summary>
    private static string MixColor((byte R, byte G, byte B, byte A) a, double pct, (byte R, byte G, byte B, byte A) b)
    {
        byte Mix(byte x, byte y) => (byte)Math.Round(x * pct + y * (1 - pct));
        return $"#{Mix(a.R, b.R):X2}{Mix(a.G, b.G):X2}{Mix(a.B, b.B):X2}";
    }

    /// <summary>SVG 字符串 → Avalonia Image。经 SKSvg.FromSvg 解析成 SKPicture，光栅化到 SKBitmap，
    /// 像素拷进 WriteableBitmap 显示。用主 SkiaSharp 3.119.4，与桌宠 Lottie 同一渲染栈。</summary>
    /// <remarks>
    /// **2x 超采样（HiDPI）**：Retina 屏上 1:1 光栅化的位图被 Avalonia 放大 2x → 文字/线条发虚。
    /// 渲 2x 像素 + WriteableBitmap DPI=192 → DIP 尺寸不变（=SVG 自然坐标宽 w）但像素密度 2x → Retina 上
    /// 1:1 物理像素渲染，清晰；1x 屏 Avalonia 降采样，无害。同桌宠 Lottie 的 WriteableBitmap 范式
    ///（PixelFormat.Rgba8888/Unpremul + Marshal.Copy SKBitmap.Bytes）。
    /// </remarks>
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

            // 2x 超采样：渲染 2x 像素，DPI 设 192（=96*2）→ DIP 尺寸 = w*h（自然），像素密度 2x。
            const int scale = 2;
            var pw = w * scale;
            var ph = h * scale;
            using var bitmap = new SKBitmap(new SKImageInfo(pw, ph, SKColorType.Rgba8888, SKAlphaType.Unpremul));
            using (var canvas = new SKCanvas(bitmap))
            {
                canvas.Clear(dark ? new SKColor(0x1e, 0x1e, 0x1e) : SKColors.White);
                canvas.Scale(scale);
                // 负原点 SVG（内容向左/上延伸）平移回位图原点。
                if (bounds.Left != 0 || bounds.Top != 0)
                    canvas.Translate((float)-bounds.Left, (float)-bounds.Top);
                canvas.DrawPicture(picture);
            }

            var dpi = 96 * scale;
            var display = new WriteableBitmap(
                new PixelSize(pw, ph), new Vector(dpi, dpi), PixelFormat.Rgba8888, AlphaFormat.Unpremul);
            // SKBitmap(Rgba8888 Unpremul) → WriteableBitmap(Rgba8888 Unpremul) 直接像素拷贝（同桌宠 CopyFrameToDisplay）。
            using (var fb = display.Lock())
            {
                var dst = fb.Address;
                var bytes = bitmap.Bytes;
                if (fb.RowBytes == bitmap.RowBytes)
                {
                    Marshal.Copy(bytes, 0, dst, bytes.Length);
                }
                else
                {
                    // 行距不一致（理论上 Rgba8888 无 padding 时一致）→ 逐行拷贝兜底。
                    var widthBytes = Math.Min(fb.RowBytes, bitmap.RowBytes);
                    var row = new byte[widthBytes];
                    for (var y = 0; y < ph; y++)
                    {
                        Marshal.Copy(bitmap.Bytes, (int)bitmap.RowBytes * y, dst + (int)(fb.RowBytes * y), (int)widthBytes);
                    }
                }
            }

            return new Image
            {
                Source = display,
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

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
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
            // 展平 CSS 变量 → 拉直折线边 → 重定位边标签 → 光栅化。
            // Mermaider SVG 用 CSS 自定义属性（var(--_xxx)）+ color-mix() 表达所有形状填色，
            // Svg.Skia 的 CSS 引擎不解析 var()/color-mix() → fill="var(--_node-fill)" 无效色 → 黑块。
            // 喂 SKSvg 前展平成具体色。失败兜底用原 SVG（最坏回退黑块→ProvideControl 返 null→默认代码块）。
            try { svg = FlattenCssVars(svg); }
            catch { /* 展平失败不阻断，SvgToImage 自会兜底 */ }
            // dagre 对含回环边/跨秩长边的图会把本可一行连直的链路节点挤到另一行，导致相邻列节点间的边
            // 变成「下→右」肘弯折线（用户反馈「本来能直直的过来，它非要拐一下，似乎是占位的问题」）。
            // 这是 dagre 排序算法固有行为（非解析 bug），无法经配置消除。后处理：把满足条件的相邻列折线边
            // 拉直成单段直线（出口点→入口点），跳过回环/跨秩长边（须绕弯）+ 跨节点边（拉直会穿节点）。
            try { svg = StraightenEdges(svg); }
            catch { /* 后处理失败不阻断 */ }
            // Mermaider 把边标签（edge-label）放在 bounding-box 中心而非路径弧长中点，
            // 横折/回环边的标签会浮在路径外的空白处（用户反馈「画线中间的文字跑外面去了」）。
            // 后处理：把每个 edge-label 平移到其 edge path 的弧长中点上，让标签压在线上。
            // 在 StraightenEdges 之后跑——拉直后的 d 仍是合法 path，标签落在直线中点。
            try { svg = RepositionEdgeLabels(svg); }
            catch { /* 后处理失败不阻断 */ }
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

        var flattened = Regex.Replace(svg, @"var\(\s*--([\w-]+)\s*(?:,[^()]*)?\)", m =>
        {
            var name = m.Groups[1].Value;
            return resolved.TryGetValue(name, out var v) && !string.IsNullOrEmpty(v) ? v : m.Value;
        });

        // 删掉整个 <style> 块：展平后所有形状的填色/字号已是 inline 具体值（fill="#xxx" / font-size="14px"），
        // style 块残留的 filter:drop-shadow() + color-mix()/var() 定义会让 Svg.Skia 的 CSS 解析器噎住
        // → 文字不渲染（<text> 的 font-family 只在 style 块里、无 inline）+ 节点形状残缺（filter 破坏绘制）。
        // 实测：删 style 块后 light 文字像素 70→673、节点像素 1531→37192（dark 同样从不可见→全渲染）。
        // Svg.Skia 用默认字体渲染 <text>（Helvetica 族，实测 macOS 字体解析成功），无需补 inline font-family。
        return Regex.Replace(flattened, @"<style[^>]*>[\s\S]*?</style>", "");
    }

    /// <summary>
    /// 把相邻列节点间的折线边（肘弯「下→右」/「上→右」）拉直成单段直线段（出口点→入口点）。
    /// 动机：dagre 对含回环边/跨秩长边的图会把链路中间节点挤到另一行（占位问题），相邻列节点间的边
    /// 本可一行连直却被迫肘弯（用户反馈「本来能直直的过来，它非要拐一下」）。这是 dagre 排序算法固有行为，
    /// 无法经配置消除，故在 SVG 后处理层拉直。仅拉直满足全部条件的边，回环/跨秩长边保留 dagre 的绕弯路径。
    /// </summary>
    /// <remarks>
    /// 拉直条件（全部满足才改）：
    /// <list type="bullet">
    /// <item>两端节点都有可解析的 <c>&lt;rect&gt;</c>（矩形节点；非矩形节点跳过——边保留 dagre 原路径，不回归）。</item>
    /// <item>无反向边（<c>tgt→src</c> 不存在）——避免 2 节点环双向边拉成同一条直线重叠（环的一边须绕弯）。</item>
    /// <item>相邻列：沿主轴（水平/垂直）无其它节点中心严格落在 src、tgt 之间——跳过跨秩长边（如回环 S→M，
    /// 中间夹 R），这类边拉直会跨过中间节点或与正向流混叠，须保留绕弯。</item>
    /// <item>直线段不穿过任何其它节点矩形（<see cref="SegHitsRect"/>）——绕过 dagre 为避让节点而设的弯。</item>
    /// </list>
    /// 出口/入口点按主轴方向选节点「面对面」的边中点：水平流向 src 右/左中 → tgt 左/右中；垂直流向 src 下/上中 → tgt 上/下中。
    /// 只替换 <c>d</c> 属性，stroke/marker/dash 等不变——箭头 marker-end 的 <c>orient="auto"</c> 按新终点切线自动定向。
    /// 之后 <see cref="RepositionEdgeLabels"/> 在新 <c>d</c> 上算弧长中点（直线中点=几何中点）定位标签。
    /// 不改动已是直线的边（重算结果与原直线一致，无害）。任何条件不满足→该边原样保留。
    /// </remarks>
    private static string StraightenEdges(string svg)
    {
        // 节点：id → 矩形 (x,y,w,h)。只收 <rect> 节点（flowchart 主流；非矩形节点不参与，保守跳过）。
        var nodes = new Dictionary<string, (double x, double y, double w, double h)>(StringComparer.Ordinal);
        foreach (Match nm in Regex.Matches(svg, @"<g\b[^>]*\bclass=""node""[^>]*>[\s\S]*?</g>"))
        {
            var block = nm.Value;
            var id = Regex.Match(block, @"data-id=""([^""]*)""").Groups[1].Value;
            if (string.IsNullOrEmpty(id)) continue;
            var rectM = Regex.Match(block, @"<rect\b[^>]*/?>");
            if (!rectM.Success) continue;
            var r = rectM.Value;
            var x = ParseDouble(Regex.Match(r, @"\bx=""([0-9.]+)""").Groups[1].Value);
            var y = ParseDouble(Regex.Match(r, @"\by=""([0-9.]+)""").Groups[1].Value);
            var w = ParseDouble(Regex.Match(r, @"\bwidth=""([0-9.]+)""").Groups[1].Value);
            var h = ParseDouble(Regex.Match(r, @"\bheight=""([0-9.]+)""").Groups[1].Value);
            nodes[id] = (x, y, w, h);
        }
        if (nodes.Count == 0) return svg;

        // 收集所有边对（data-from|data-to），供反向边检测。
        var edgePairs = new HashSet<string>(StringComparer.Ordinal);
        foreach (Match em in Regex.Matches(svg, @"<path\b[^>]*\bclass=""edge""[^>]*>"))
        {
            var f = Regex.Match(em.Value, @"data-from=""([^""]*)""").Groups[1].Value;
            var t = Regex.Match(em.Value, @"data-to=""([^""]*)""").Groups[1].Value;
            edgePairs.Add(f + "|" + t);
        }
        if (edgePairs.Count == 0) return svg;

        foreach (Match em in Regex.Matches(svg, @"<path\b[^>]*\bclass=""edge""[^>]*>"))
        {
            var pathTag = em.Value;
            var from = Regex.Match(pathTag, @"data-from=""([^""]*)""").Groups[1].Value;
            var to = Regex.Match(pathTag, @"data-to=""([^""]*)""").Groups[1].Value;
            if (!nodes.TryGetValue(from, out var src) || !nodes.TryGetValue(to, out var tgt)) continue;
            // 无反向边——2 节点环双向边拉直会重叠，须保留 dagre 的绕弯。
            if (edgePairs.Contains(to + "|" + from)) continue;

            var (sx, sy, sw, sh) = src;
            var (tx, ty, tw, th) = tgt;
            var scx = sx + sw / 2; var scy = sy + sh / 2;
            var tcx = tx + tw / 2; var tcy = ty + th / 2;

            double ex, ey, entryX, entryY;
            bool horiz = Math.Abs(tcx - scx) >= Math.Abs(tcy - scy);
            if (horiz)
            {
                var right = tcx > scx;
                ex = right ? sx + sw : sx; ey = scy;            // src 右/左中
                entryX = right ? tx : tx + tw; entryY = tcy;    // tgt 左/右中
                if (NodeCenterBetween(nodes, from, to, scx, tcx, true)) continue;
            }
            else
            {
                var down = tcy > scy;
                ex = scx; ey = down ? sy + sh : sy;            // src 下/上中
                entryX = tcx; entryY = down ? ty : ty + th;    // tgt 上/下中
                if (NodeCenterBetween(nodes, from, to, scy, tcy, false)) continue;
            }

            // 直线段不得穿过任何其它节点矩形（否则保留 dagre 为避让设的弯）。
            bool crosses = false;
            foreach (var kv in nodes)
            {
                if (kv.Key == from || kv.Key == to) continue;
                var n = kv.Value;
                if (SegHitsRect(ex, ey, entryX, entryY, n.x, n.y, n.x + n.w, n.y + n.h))
                { crosses = true; break; }
            }
            if (crosses) continue;

            var newD = "M " + ex.ToString("0.####", CultureInfo.InvariantCulture) + ","
                              + ey.ToString("0.####", CultureInfo.InvariantCulture)
                     + " L " + entryX.ToString("0.####", CultureInfo.InvariantCulture) + ","
                              + entryY.ToString("0.####", CultureInfo.InvariantCulture);
            var newPathTag = Regex.Replace(pathTag, @"\bd=""[^""]*""", "d=\"" + newD + "\"");
            svg = svg.Replace(pathTag, newPathTag);
        }
        return svg;
    }

    /// <summary>沿主轴是否有其它节点中心严格落在 <paramref name="a"/>、<paramref name="b"/> 之间
    /// （<paramref name="horizontal"/> true 比 cx，false 比 cy）。有→该边非相邻列（跨秩长边），应跳过拉直。</summary>
    private static bool NodeCenterBetween(
        Dictionary<string, (double x, double y, double w, double h)> nodes,
        string from, string to, double a, double b, bool horizontal)
    {
        var lo = Math.Min(a, b); var hi = Math.Max(a, b);
        foreach (var kv in nodes)
        {
            if (kv.Key == from || kv.Key == to) continue;
            var c = horizontal ? kv.Value.x + kv.Value.w / 2 : kv.Value.y + kv.Value.h / 2;
            if (c > lo + 0.5 && c < hi - 0.5) return true;
        }
        return false;
    }

    /// <summary>线段 (x0,y0)→(x1,y1) 是否与轴对齐矩形 [xmin,ymin,xmax,ymax] 相交（Liang-Barsky）。
    /// 用于拉直前的穿节点守卫——直线段若穿过其它节点矩形则保留 dagre 的绕弯路径。</summary>
    private static bool SegHitsRect(double x0, double y0, double x1, double y1,
        double xmin, double ymin, double xmax, double ymax)
    {
        if (Math.Max(x0, x1) < xmin || Math.Min(x0, x1) > xmax) return false;
        if (Math.Max(y0, y1) < ymin || Math.Min(y0, y1) > ymax) return false;
        double dx = x1 - x0, dy = y1 - y0, t0 = 0.0, t1 = 1.0;
        bool Clip(double p, double q)
        {
            if (Math.Abs(p) < 1e-9) return q >= 0;       // 平行：在外则不相交
            var r = q / p;
            if (p < 0) { if (r > t1) return false; if (r > t0) t0 = r; }
            else { if (r < t0) return false; if (r < t1) t1 = r; }
            return true;
        }
        if (!Clip(-dx, x0 - xmin)) return false;
        if (!Clip(dx, xmax - x0)) return false;
        if (!Clip(-dy, y0 - ymin)) return false;
        if (!Clip(dy, ymax - y0)) return false;
        return t0 <= t1;
    }

    /// <summary>
    /// 把每个边标签（<c>&lt;g class="edge-label"&gt;</c>）平移到其对应 edge path 的弧长中点上。
    /// Mermaider 把边标签放在边 bounding-box 的中心而非路径弧长中点，导致横折边（先横走再折下）与回环边
    /// 的标签浮在路径外的空白处——用户反馈「画线中间的文字跑外面去了」。本方法解析每条 edge path 的 <c>d</c>
    /// 属性（M/L 直线 + Q/C 曲线采样），算弧长中点，给标签 <c>&lt;g&gt;</c> 注入 <c>transform="translate(dx,dy)"</c>
    /// 让标签压在线上。标签 pill 的不透明背景（<c>fill=var(--bg)</c>）会盖住线段，呈标准 mermaid 标签外观。
    /// 已在线上的标签（直边）平移量≈0 不受影响。找不到对应 path 的标签原样保留。
    /// </summary>
    private static string RepositionEdgeLabels(string svg)
    {
        var labelBlocks = Regex.Matches(svg, @"<g class=""edge-label""[^>]*>[\s\S]*?</g>");
        if (labelBlocks.Count == 0) return svg;

        var result = svg;
        foreach (Match label in labelBlocks)
        {
            var from = Regex.Match(label.Value, @"data-from=""([^""]*)""").Groups[1].Value;
            var to = Regex.Match(label.Value, @"data-to=""([^""]*)""").Groups[1].Value;
            // 用 data-from + data-to 定位对应 edge path（class="edge"）。\bd= 避开 data-xxx 里的 d=。
            var pathTag = Regex.Match(svg,
                @"<path\b[^>]*\bclass=""edge""[^>]*\bdata-from=""" + Regex.Escape(from) + @"""[^>]*\bdata-to=""" + Regex.Escape(to) + @"""[^>]*>");
            var dMatch = Regex.Match(pathTag.Value, @"\bd=""([^""]*)""");
            if (!pathTag.Success || !dMatch.Success) continue;

            var mid = MidpointOnPath(dMatch.Groups[1].Value);
            if (double.IsNaN(mid.x) || double.IsNaN(mid.y)) continue;

            // 标签 pill 中心 = rect 的几何中心。
            var rect = Regex.Match(label.Value, @"<rect\b[^>]*/>");
            if (!rect.Success) continue;
            var cx = ParseDouble(Regex.Match(rect.Value, @"\bx=""([0-9.]+)""").Groups[1].Value)
                     + ParseDouble(Regex.Match(rect.Value, @"\bwidth=""([0-9.]+)""").Groups[1].Value) / 2;
            var cy = ParseDouble(Regex.Match(rect.Value, @"\by=""([0-9.]+)""").Groups[1].Value)
                     + ParseDouble(Regex.Match(rect.Value, @"\bheight=""([0-9.]+)""").Groups[1].Value) / 2;

            var dx = mid.x - cx;
            var dy = mid.y - cy;
            // 平移量太小（<0.5px）不注入——避免无谓属性改动。
            if (Math.Abs(dx) < 0.5 && Math.Abs(dy) < 0.5) continue;

            var gOpen = Regex.Match(label.Value, @"<g class=""edge-label""[^>]*>").Value;
            var transform = "translate(" + dx.ToString("0.####", CultureInfo.InvariantCulture)
                            + "," + dy.ToString("0.####", CultureInfo.InvariantCulture) + ")";
            string newOpen;
            if (gOpen.Contains("transform="))
                newOpen = Regex.Replace(gOpen, @"transform=""[^""]*""", "transform=\"" + transform + "\"");
            else
                newOpen = gOpen.Replace(">", " transform=\"" + transform + "\">");
            var newBlock = newOpen + label.Value.Substring(gOpen.Length);
            result = result.Replace(label.Value, newBlock);
        }
        return result;
    }

    /// <summary>解析 SVG path <c>d</c> 属性，返回沿弧长排列的采样点序列（含累计弧长 <c>s</c>）。
    /// M/L 取端点；Q 二次贝塞尔/C 三次贝塞尔各采样 12/16 点逼近弧长。</summary>
    private static List<(double x, double y, double s)> ParsePathPoints(string d)
    {
        var pts = new List<(double x, double y)>();
        var curX = 0.0; var curY = 0.0;
        foreach (Match seg in Regex.Matches(d, @"([MLQCmlqc])\s*([^MLQCmlqc]*)"))
        {
            var cmd = seg.Groups[1].Value;
            var cv = Regex.Matches(seg.Groups[2].Value, @"[-+]?\d+\.?\d*")
                .Cast<Match>().Select(x => ParseDouble(x.Value)).ToList();
            if (cmd == "M" && cv.Count >= 2) { curX = cv[0]; curY = cv[1]; pts.Add((curX, curY)); }
            else if (cmd == "L" && cv.Count >= 2) { curX = cv[0]; curY = cv[1]; pts.Add((curX, curY)); }
            else if ((cmd == "Q" || cmd == "q") && cv.Count >= 4)
            {
                var (p0x, p0y) = (curX, curY); var (p1x, p1y) = (cv[0], cv[1]); var (p2x, p2y) = (cv[2], cv[3]);
                for (var i = 1; i <= 12; i++)
                {
                    var t = (double)i / 12; var mt = 1 - t;
                    curX = mt * mt * p0x + 2 * mt * t * p1x + t * t * p2x;
                    curY = mt * mt * p0y + 2 * mt * t * p1y + t * t * p2y;
                    pts.Add((curX, curY));
                }
            }
            else if ((cmd == "C" || cmd == "c") && cv.Count >= 6)
            {
                var (p0x, p0y) = (curX, curY); var (p1x, p1y) = (cv[0], cv[1]);
                var (p2x, p2y) = (cv[2], cv[3]); var (p3x, p3y) = (cv[4], cv[5]);
                for (var i = 1; i <= 16; i++)
                {
                    var t = (double)i / 16; var mt = 1 - t;
                    curX = mt * mt * mt * p0x + 3 * mt * mt * t * p1x + 3 * mt * t * t * p2x + t * t * t * p3x;
                    curY = mt * mt * mt * p0y + 3 * mt * mt * t * p1y + 3 * mt * t * t * p2y + t * t * t * p3y;
                    pts.Add((curX, curY));
                }
            }
        }
        var result = new List<(double x, double y, double s)>();
        if (pts.Count == 0) return result;
        var s = 0.0;
        result.Add((pts[0].x, pts[0].y, 0));
        for (var i = 1; i < pts.Count; i++)
        {
            s += Math.Sqrt((pts[i].x - pts[i - 1].x) * (pts[i].x - pts[i - 1].x)
                          + (pts[i].y - pts[i - 1].y) * (pts[i].y - pts[i - 1].y));
            result.Add((pts[i].x, pts[i].y, s));
        }
        return result;
    }

    /// <summary>算 path 弧长中点坐标（总弧长一半处的点）。</summary>
    private static (double x, double y) MidpointOnPath(string d)
    {
        var pts = ParsePathPoints(d);
        if (pts.Count == 0) return (double.NaN, double.NaN);
        if (pts.Count == 1) return (pts[0].x, pts[0].y);
        var target = pts.Last().s / 2;
        for (var i = 1; i < pts.Count; i++)
        {
            if (pts[i].s >= target)
            {
                var segLen = pts[i].s - pts[i - 1].s;
                var t = segLen > 0 ? (target - pts[i - 1].s) / segLen : 0;
                return (pts[i - 1].x + t * (pts[i].x - pts[i - 1].x),
                        pts[i - 1].y + t * (pts[i].y - pts[i - 1].y));
            }
        }
        return (pts.Last().x, pts.Last().y);
    }

    private static double ParseDouble(string s) => double.Parse(s, CultureInfo.InvariantCulture);

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

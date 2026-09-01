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
using Avalonia.Svg.Skia;
using Mermaider.Models;
using SkiaSharp;

namespace Acme.Desktop.Shared.Shell.Controls;

/// <summary>
/// mermaid 图渲染器：把 mermaid 源码经 Mermaider（纯 .NET，无浏览器/JS，输出 SVG 字符串）渲染，
/// 再经继任包 Svg.Controls.Skia.Avalonia 的 SvgImage（IImage）直显矢量（任意 DPI 清晰，免光栅）——SvgSource.LoadFromSvg
/// 直吃 SVG 字符串、SvgImage 取 SKPicture 矢量渲染，喂标准 Avalonia Image 控件（Image.Source 收 IImage）。
/// 经 Markdown.Avalonia 的 IContainerBlockHandler 扩展点注入：拦截 <c>::: mermaid</c> 容器块，不换 markdown 引擎、
/// 不改 markdown 文本（标准 ```mermaid 由 SelectableMarkdownViewer 预处理转 <c>::: mermaid</c>）。
/// 主题感知：按 ActualThemeVariant 选 light/dark 配色，切换后由调用方重渲染刷新。
/// </summary>
/// <remarks>
/// 用继任包 Svg.Controls.Skia.Avalonia（12.0.0.17，活跃、正对 Avalonia 12.x）的 SvgImage + SvgSource.LoadFromSvg
/// 直显矢量，取代旧光栅路（SKSvg→SKPicture→SKBitmap→PNG→Bitmap）。升级链：Svg.Controls.Skia.Avalonia→Svg.Skia>=5.2.3→
/// SkiaSharp>=4.148.0（4.x）；SkiaSharp 4.x 经下界兼容（Avalonia.Skia 12.1.1 / RoyalTerminal 0.5.2 / Skottie 4.151.1 均
/// >=3.119.4 下界，4.x 满足）。旧 deprecated Avalonia.Svg.Skia 的 CropRect/TypeLoadException 故事在 Svg.Skia 5.x 重写后已不成立。
/// </remarks>
internal static class MermaidSvgRenderer
{
    /// <summary>把 mermaid 源码渲染成 Avalonia Image 控件（Source = SvgImage 矢量）。失败返回 null（调用方回退默认代码块渲染，不崩）。</summary>
    public static Image? Render(string mermaidSource, bool dark)
    {
        try
        {
            var svg = Mermaider.MermaidRenderer.RenderSvg(mermaidSource, BuildOptions(dark));
            if (string.IsNullOrWhiteSpace(svg)) return null;
            // 展平 CSS 变量 → 拉直折线边 → 重定位边标签 → 矢量化。
            // Mermaider SVG 用 CSS 自定义属性（var(--_xxx)）+ color-mix() 表达所有形状填色，
            // Svg.Skia 的 CSS 引擎不解析 var()/color-mix() → fill="var(--_node-fill)" 无效色 → 黑块。
            // 喂 SvgSource 前展平成具体色。失败兜底用原 SVG（最坏回退黑块→ProvideControl 返 null→默认代码块）。
            try { svg = FlattenCssVars(svg); }
            catch { /* 展平失败不阻断，BuildSvgControl 自会兜底 */ }
            // dagre 对含回环边的图（如 A→B→C→A）为给回环边留纵向 lane，会把本可同行的链路中间节点（B）
            // 挤到另一行 → A、C 同行而 B 独占一行 → 整图呈 V/Λ 型（用户反馈「三个节点却是 V 型」）。
            // StraightenEdges 只改边路径 d、不动节点坐标，纠正不了这种节点错位。故先跑本步把 B 拉回 A、C
            // 所在行，再由 StraightenEdges 把 A→B、B→C 改写成单段直线（同行即水平直线）。回环边（C→A）
            // 经 StraightenEdges 的 NodeCenterBetween 守卫自动跳过，保留 dagre 绕弯路径，不受影响。
            try { svg = AlignDroppedChainNodes(svg); }
            catch { /* 对齐失败不阻断 */ }
            // AlignDroppedChainNodes 把被 dagre 挤下行的链路中间节点拉回同行后，回环边（C→A / S→M）的路径
            // 仍是 dagre 按旧（下行）布局路由的——其底部横段还停在原来给下行节点留纵向 lane 的深处，于是回环边
            // 在紧凑节点簇下方远远探出一条空荡长线（用户反馈「闭环框框，里面的元素超出了一点点，就那个反馈
            // 闭环的那根线」）。mermaid.js 用配置良好的 dagre 不会出现——其 dagre 不下行挤节点，回环边天然贴簇；
            // 我们的 dagre port（Mermaider）无 ranker 旋钮，故在后处理层把回环边压紧成贴簇紧凑矩形回环。
            // 必须在 Align 后、StraightenEdges 前跑：Align 后节点已紧凑、簇外缘稳定；StraightenEdges 只改正向边
            // （其反向边守卫跳过回环边），不影响本步改的回环边 d。
            try { svg = CompactBackEdges(svg); }
            catch { /* 压紧失败不阻断 */ }
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
            return BuildSvgControl(svg);
        }
        catch
        {
            // 渲染失败（语法错/不支持图型/Svg.Skia 与 SkiaSharp 4.151.1 的其它 API 差异）→ null，
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
    /// 把 dagre 因排布回环边而挤到另一行的链路中间节点拉回与前后节点同行的位置。
    /// 动机：dagre 对含回环边的图（如 <c>A→B→C→A</c>）为给回环边留纵向 lane，会把本可同行的链路中间节点
    /// （B）挤到另一行 → A、C 同行而 B 独占一行 → 整图呈 V/Λ 型（用户反馈「三个节点却是 V 型」）。
    /// <see cref="StraightenEdges"/> 只改边路径 <c>d</c>、不动节点坐标，纠正不了这种节点错位。本方法在它之前跑：
    /// 找出「恰好一进一出」的链路中间节点 B，若其前驱 A、后继 C 同行而 B 被挤下/侧，把 B 平移回 A、C 所在行；
    /// 随后 <see cref="StraightenEdges"/> 把 A→B、B→C 改写成单段直线（同行即水平/垂直直线）。回环边（如 C→A）
    /// 经 <see cref="StraightenEdges"/> 的 <see cref="NodeCenterBetween"/> 守卫自动跳过，保留 dagre 的绕弯路径，不受影响。
    /// </summary>
    /// <remarks>
    /// 触发条件（全部满足才平移，保守不破坏合法布局）：
    /// <list type="bullet">
    /// <item>B 恰好一进一出（preds.Count==1 &amp;&amp; succs.Count==1）——纯链路中间节点；合并/分叉点不动。</item>
    /// <item>前驱 A、后继 C 在垂直/水平轴上同行（|cA-cC| &lt; 2px），B 在该轴上明显错位（偏移 &gt; 半个 B 高/宽）。</item>
    /// <item>B 沿链路方向严格夹在 A、C 之间（中心坐标单调）——确认是真链路而非巧合同行。</item>
    /// <item>B 平移到目标行后不与任何其它节点矩形重叠（AABB 检测）——不覆盖别的节点。</item>
    /// </list>
    /// 水平链纠 Y 错位、垂直链纠 X 错位，两轴对称（TD 回环同理）。只改节点 <c>&lt;g&gt;</c> 内的 <c>x=</c>/<c>y=</c>
    /// 坐标属性，不动 edge path（交给 <see cref="StraightenEdges"/> 重写）。任何条件不满足→该节点原样保留。
    /// </remarks>
    private static string AlignDroppedChainNodes(string svg)
    {
        // 节点：id → 矩形 (x,y,w,h)，只收 <rect> 节点（与 StraightenEdges 同口径）。
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

        // 邻接表：每节点的一进一出边。
        var ins = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        var outs = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (Match em in Regex.Matches(svg, @"<path\b[^>]*\bclass=""edge""[^>]*>"))
        {
            var f = Regex.Match(em.Value, @"data-from=""([^""]*)""").Groups[1].Value;
            var t = Regex.Match(em.Value, @"data-to=""([^""]*)""").Groups[1].Value;
            if (!outs.ContainsKey(f)) outs[f] = new List<string>();
            if (!ins.ContainsKey(t)) ins[t] = new List<string>();
            outs[f].Add(t);
            ins[t].Add(f);
        }

        double Cx(string id) => nodes[id].x + nodes[id].w / 2;
        double Cy(string id) => nodes[id].y + nodes[id].h / 2;

        foreach (var kv in nodes)
        {
            var b = kv.Key;
            var hasIn = ins.TryGetValue(b, out var preds) && preds.Count == 1;
            var hasOut = outs.TryGetValue(b, out var succs) && succs.Count == 1;
            if (!hasIn || !hasOut) continue;
            var a = preds[0];
            var c = succs[0];
            if (a == b || c == b || a == c) continue;
            if (!nodes.ContainsKey(a) || !nodes.ContainsKey(c)) continue;
            var B = nodes[b];

            // 水平链：A、C 同行（cy 近等），B 在 Y 上被挤开（偏移 > 半个 B 高），且 B 的 cx 严格夹在 A、C 之间。
            if (Math.Abs(Cy(a) - Cy(c)) < 2.0 && Math.Abs(Cy(b) - Cy(a)) > 0.5 * B.h
                && ((Cx(a) < Cx(b) && Cx(b) < Cx(c)) || (Cx(a) > Cx(b) && Cx(b) > Cx(c))))
            {
                var targetCy = (Cy(a) + Cy(c)) / 2;
                var dy = targetCy - Cy(b);
                if (!NodeCollidesAt(nodes, b, B.x, B.y + dy, B.w, B.h))
                    svg = ShiftNodeCoord(svg, b, 0, dy);
            }
            // 垂直链：A、C 同列（cx 近等），B 在 X 上被挤开（偏移 > 半个 B 宽），且 B 的 cy 严格夹在 A、C 之间。
            else if (Math.Abs(Cx(a) - Cx(c)) < 2.0 && Math.Abs(Cx(b) - Cx(a)) > 0.5 * B.w
                && ((Cy(a) < Cy(b) && Cy(b) < Cy(c)) || (Cy(a) > Cy(b) && Cy(b) > Cy(c))))
            {
                var targetCx = (Cx(a) + Cx(c)) / 2;
                var dx = targetCx - Cx(b);
                if (!NodeCollidesAt(nodes, b, B.x + dx, B.y, B.w, B.h))
                    svg = ShiftNodeCoord(svg, b, dx, 0);
            }
        }
        return svg;
    }

    /// <summary>B 的矩形移到 (nx,ny,w,h) 后是否与任何其它节点矩形重叠（AABB：任一轴分离即不重叠）。</summary>
    private static bool NodeCollidesAt(
        Dictionary<string, (double x, double y, double w, double h)> nodes,
        string self, double nx, double ny, double w, double h)
    {
        foreach (var kv in nodes)
        {
            if (kv.Key == self) continue;
            var n = kv.Value;
            if (!(nx + w <= n.x || nx >= n.x + n.w || ny + h <= n.y || ny >= n.y + n.h))
                return true;
        }
        return false;
    }

    /// <summary>把节点 <c>&lt;g&gt;</c> 块内所有 <c>x="num"</c>/<c>y="num"</c> 属性平移 (dx,dy)。
    /// <c>\bx=</c>/<c>\by=</c> 的词边界避开 <c>dx/dy/rx/ry/cx/cy/width/height</c> 等；<c>dy=</c>/<c>dx=</c>
    /// （文字基线相对偏移）不被匹配——它是相对量不该平移。只动节点块，edge path 不碰（交给 StraightenEdges）。</summary>
    private static string ShiftNodeCoord(string svg, string id, double dx, double dy)
    {
        var blockM = Regex.Match(svg,
            @"<g\b[^>]*\bclass=""node""[^>]*\bdata-id=""" + Regex.Escape(id) + @"""[^>]*>[\s\S]*?</g>");
        if (!blockM.Success) return svg;
        var block = blockM.Value;
        var shifted = Regex.Replace(block, @"(\b[xy]="")(?:-?[0-9.]+)("")", m =>
        {
            var isX = m.Value[0] == 'x';
            var val = ParseDouble(Regex.Match(m.Value, @"-?[0-9.]+").Value);
            var nv = val + (isX ? dx : dy);
            return m.Groups[1].Value + nv.ToString("0.####", CultureInfo.InvariantCulture) + m.Groups[2].Value;
        });
        return svg.Replace(block, shifted);
    }

    /// <summary>
    /// 把回环边（back-edge，如 A→B→C→A 里的 C→A、BASE→M→R→S→M 里的 S→M）dagre 为绕回而路由出的
    /// 「下→横→上」长肘弯压紧成贴近节点簇的紧凑矩形回环。<see cref="AlignDroppedChainNodes"/> 把被 dagre
    /// 挤下行的链路中间节点拉回同行后，回环边的路径仍是按旧（下行）布局路由的——其底部横段还停在原来给下行
    /// 节点留纵向 lane 的深处，于是回环边在紧凑节点簇下方远远探出一条空荡长线（用户反馈「闭环框框，里面的
    /// 元素超出了一点点，就那个反馈闭环的那根线」）。mermaid.js 用配置良好的 dagre 不会出现（其 dagre 不下行
    /// 挤节点，回环边天然贴簇）；Mermaider 无 ranker 旋钮，故在后处理层把回环边压紧——等效于 mermaid.js 的
    /// 紧凑回环路由。
    /// </summary>
    /// <remarks>
    /// 触发条件（全部满足才压紧，保守不破坏合法布局）：
    /// <list type="bullet">
    /// <item>边路径某轴向极值（LR 图 maxY/minY，TB 图 maxX/minX）越过节点簇同向外缘 + 阈值（显著探出，
    /// 阈值=max(12,0.4×簇该轴尺寸)，避免误碰正常边距）。</item>
    /// <item>回路判定：起点与终点在连接轴上同位（|start.{connAxis} - end.{connAxis}| &lt; r+2）——回环边从某
    /// 节点出、绕一圈回到同行/同列另一节点；正向边（到被挤下行的节点）终点在另一行/列，不满足故跳过
    /// （其 elbow 由 <see cref="StraightenEdges"/> 拉直处理）。</item>
    /// <item>路径呈标准 dagre 回环肘弯 M,L,Q,L,Q,L（六段），可解析出横段两端点 + 角半径 r∈[2,25]。</item>
    /// <item>压紧后连接段仍有 ≥4px 余量（不反向）、且实际缩短 ≥8px（有意义才改）。</item>
    /// </list>
    /// 四向对称：below/above（LR 图回环边绕簇下/上）、right/left（TB 图回环边绕簇右/左）。只改该回环边 d，
    /// 节点及其它边不动；正向边由 <see cref="StraightenEdges"/> 另处理。任何条件不满足→该边原样保留。
    /// </remarks>
    private static string CompactBackEdges(string svg)
    {
        var nodes = new Dictionary<string, (double x, double y, double w, double h)>(StringComparer.Ordinal);
        foreach (Match nm in Regex.Matches(svg, @"<g\b[^>]*\bclass=""node""[^>]*>[\s\S]*?</g>"))
        {
            var block = nm.Value;
            var id = Regex.Match(block, @"data-id=""([^""]*)""").Groups[1].Value;
            if (string.IsNullOrEmpty(id)) continue;
            var rectM = Regex.Match(block, @"<rect\b[^>]*/?>");
            if (!rectM.Success) continue;
            var r = rectM.Value;
            nodes[id] = (ParseDouble(Regex.Match(r, @"\bx=""([0-9.]+)""").Groups[1].Value),
                        ParseDouble(Regex.Match(r, @"\by=""([0-9.]+)""").Groups[1].Value),
                        ParseDouble(Regex.Match(r, @"\bwidth=""([0-9.]+)""").Groups[1].Value),
                        ParseDouble(Regex.Match(r, @"\bheight=""([0-9.]+)""").Groups[1].Value));
        }
        if (nodes.Count == 0) return svg;

        var cMinX = nodes.Values.Min(n => n.x);
        var cMaxX = nodes.Values.Max(n => n.x + n.w);
        var cMinY = nodes.Values.Min(n => n.y);
        var cMaxY = nodes.Values.Max(n => n.y + n.h);
        var cW = cMaxX - cMinX;
        var cH = cMaxY - cMinY;
        var thrY = Math.Max(12.0, 0.4 * cH);
        var thrX = Math.Max(12.0, 0.4 * cW);
        // 边距：贴簇外缘 + 容纳箭头与边标签 pill（居中横段故上下/左右各 ~10px）。随簇该轴尺寸缩放（对齐后≈节点高/宽）。
        var marginY = Math.Max(24.0, 0.5 * cH);
        var marginX = Math.Max(24.0, 0.5 * cW);

        var result = svg;
        foreach (Match em in Regex.Matches(svg, @"<path\b[^>]*\bclass=""edge""[^>]*>"))
        {
            var pathTag = em.Value;
            var dMatch = Regex.Match(pathTag, @"\bd=""([^""]*)""");
            if (!dMatch.Success) continue;
            var d = dMatch.Groups[1].Value;
            var pts = ParsePathPoints(d);
            if (pts.Count < 4) continue;
            var xs = pts.Select(p => p.x).ToArray();
            var ys = pts.Select(p => p.y).ToArray();
            var pMinX = xs.Min(); var pMaxX = xs.Max();
            var pMinY = ys.Min(); var pMaxY = ys.Max();

            string? newD = null;
            if (pMaxY - cMaxY > thrY)
                newD = CompactBelow(d, cMaxY + marginY);
            else if (cMinY - pMinY > thrY)
                newD = CompactAbove(d, cMinY - marginY);
            else if (pMaxX - cMaxX > thrX)
                newD = CompactRight(d, cMaxX + marginX);
            else if (cMinX - pMinX > thrX)
                newD = CompactLeft(d, cMinX - marginX);

            if (newD is null) continue;
            var newPathTag = Regex.Replace(pathTag, @"\bd=""[^""]*""", "d=\"" + newD + "\"");
            result = result.Replace(pathTag, newPathTag);
        }
        return result;
    }

    /// <summary>解析回环边路径 d 的几何结构。dagre 回环边呈 <c>M L Q L Q L</c> 形：
    /// 起→直线段→Q 角→横/竖直段→Q 角→直线段→终。两 Q 的控制点落在 across 线上（控制点该轴坐标即横/竖段常量坐标），
    /// Q 终点是真正的角端点。旧实现用采样点找「≈across 的最长 run」算角半径，但采样点含仍在弯曲的 Q 曲线中段
    /// （x 还没到角端点）→ r 被低估（如 1.5 &lt; 2 守卫误杀）→ 回环边不压紧。改走结构化解析命令坐标，直接取
    /// Q 控制点/终点，r 准确（=6）。
    /// <para><paramref name="horizontal"/>=true（LR 图 below/above 路由，横段平行 x 轴）→ across=控制点 y、n1/n2=Q 终点/L 端点 x；</para>
    /// <para><paramref name="horizontal"/>=false（TB 图 right/left 路由，竖段平行 y 轴）→ across=控制点 x、n1/n2=Q 终点/L 端点 y。</para>
    /// 返回 (across, n1, n2, r, sx, sy, tx, ty) 或 null。</summary>
    private static (double across, double n1, double n2, double r, double sx, double sy, double tx, double ty)?
        ParseBackEdge(string d, bool horizontal)
    {
        var cmds = new List<(char cmd, double[] coords)>();
        foreach (Match seg in Regex.Matches(d, @"([MLQCmlqc])\s*([^MLQCmlqc]*)"))
        {
            if (seg.Groups[1].Value.Length == 0) continue;
            var cmd = seg.Groups[1].Value[0];
            var cv = Regex.Matches(seg.Groups[2].Value, @"[-+]?\d+\.?\d*")
                .Cast<Match>().Select(x => ParseDouble(x.Value)).ToArray();
            cmds.Add((cmd, cv));
        }
        var qs = cmds.Where(c => c.cmd == 'Q' || c.cmd == 'q').ToList();
        if (qs.Count < 2 || qs[0].coords.Length < 4 || qs[1].coords.Length < 4) return null;
        var q1 = qs[0]; var q2 = qs[1];
        // across = 首 Q 控制点的常量轴坐标（horiz→y=coords[1]，vert→x=coords[0]）
        double across = horizontal ? q1.coords[1] : q1.coords[0];
        // n1 = 首 Q 终点垂直轴坐标
        double n1 = horizontal ? q1.coords[2] : q1.coords[3];
        // n2 = 次 Q 起点垂直轴 = 两 Q 间 L 的端点（无 L 则退化为 n1）
        int iq2 = cmds.IndexOf(q2);
        double n2 = n1;
        if (iq2 > 0)
        {
            var prev = cmds[iq2 - 1];
            if ((prev.cmd == 'L' || prev.cmd == 'l') && prev.coords.Length >= 2)
                n2 = horizontal ? prev.coords[0] : prev.coords[1];
        }
        // 起点（M）+ 终点（末 L 或末 Q 终点）
        var m = cmds.FirstOrDefault(c => c.cmd == 'M' || c.cmd == 'm');
        if (m.coords == null || m.coords.Length < 2) return null;
        double sx = m.coords[0], sy = m.coords[1];
        double tx, ty;
        var last = cmds[cmds.Count - 1];
        if (last.cmd == 'L' || last.cmd == 'l') { tx = last.coords[0]; ty = last.coords[1]; }
        else if ((last.cmd == 'Q' || last.cmd == 'q') && last.coords.Length >= 4) { tx = last.coords[2]; ty = last.coords[3]; }
        else { tx = sx; ty = sy; }
        double r = horizontal ? Math.Abs(sx - n1) : Math.Abs(sy - n1);
        return (across, n1, n2, r, sx, sy, tx, ty);
    }

    /// <summary>压紧 below 路由回环边（LR 图，绕簇下方）：横段从 across 拉到 newYb=簇底+边距，连接竖段缩短。</summary>
    private static string? CompactBelow(string d, double newYb)
    {
        var g = ParseBackEdge(d, horizontal: true);
        if (g is null) return null;
        var (across, n1, n2, r, sx, sy, tx, ty) = g.Value;
        if (r < 2 || r > 25) return null;
        if (Math.Abs(sy - ty) > r + 2) return null;            // 回路：起终点同行（绕一圈回原行）
        var nearSx = Math.Abs(sx - n1) < Math.Abs(sx - n2) ? n1 : n2;
        var nearTx = Math.Abs(sx - n1) < Math.Abs(sx - n2) ? n2 : n1;
        if (Math.Abs(Math.Abs(tx - nearTx) - r) > 3) return null;  // 次角半径须≈r（一致性校验）
        if (across - newYb < 8) return null;                   // 当前横段须在目标下方且压紧≥8px
        var newDownEnd = newYb - r;
        if (newDownEnd - Math.Max(sy, ty) < 4) return null;    // 连接竖段不反向
        return $"M {F(sx)},{F(sy)} L {F(sx)},{F(newDownEnd)} Q {F(sx)},{F(newYb)} {F(nearSx)},{F(newYb)} "
             + $"L {F(nearTx)},{F(newYb)} Q {F(tx)},{F(newYb)} {F(tx)},{F(newDownEnd)} L {F(tx)},{F(ty)}";
    }

    /// <summary>压紧 above 路由回环边（LR 图，绕簇上方）：横段从 across 拉到 newYt=簇顶-边距，连接竖段缩短。</summary>
    private static string? CompactAbove(string d, double newYt)
    {
        var g = ParseBackEdge(d, horizontal: true);
        if (g is null) return null;
        var (across, n1, n2, r, sx, sy, tx, ty) = g.Value;
        if (r < 2 || r > 25) return null;
        if (Math.Abs(sy - ty) > r + 2) return null;
        var nearSx = Math.Abs(sx - n1) < Math.Abs(sx - n2) ? n1 : n2;
        var nearTx = Math.Abs(sx - n1) < Math.Abs(sx - n2) ? n2 : n1;
        if (Math.Abs(Math.Abs(tx - nearTx) - r) > 3) return null;
        if (newYt - across < 8) return null;   // above 路由 across>newYt（横段在上方），须 across-newYt≥8
        var newUpEnd = newYt + r;
        if (Math.Min(sy, ty) - newUpEnd < 4) return null;
        return $"M {F(sx)},{F(sy)} L {F(sx)},{F(newUpEnd)} Q {F(sx)},{F(newYt)} {F(nearSx)},{F(newYt)} "
             + $"L {F(nearTx)},{F(newYt)} Q {F(tx)},{F(newYt)} {F(tx)},{F(newUpEnd)} L {F(tx)},{F(ty)}";
    }

    /// <summary>压紧 right 路由回环边（TB 图，绕簇右方）：竖段从 across 拉到 newX=簇右+边距，连接横段缩短。</summary>
    private static string? CompactRight(string d, double newX)
    {
        var g = ParseBackEdge(d, horizontal: false);
        if (g is null) return null;
        var (across, n1, n2, r, sx, sy, tx, ty) = g.Value;
        if (r < 2 || r > 25) return null;
        if (Math.Abs(sx - tx) > r + 2) return null;            // 回路：起终点同列
        var nearSy = Math.Abs(sy - n1) < Math.Abs(sy - n2) ? n1 : n2;
        var nearTy = Math.Abs(sy - n1) < Math.Abs(sy - n2) ? n2 : n1;
        if (Math.Abs(Math.Abs(ty - nearTy) - r) > 3) return null;
        if (across - newX < 8) return null;
        var newRightEnd = newX - r;
        if (newRightEnd - Math.Max(sx, tx) < 4) return null;
        return $"M {F(sx)},{F(sy)} L {F(newRightEnd)},{F(sy)} Q {F(newX)},{F(sy)} {F(newX)},{F(nearSy)} "
             + $"L {F(newX)},{F(nearTy)} Q {F(newX)},{F(ty)} {F(newRightEnd)},{F(ty)} L {F(tx)},{F(ty)}";
    }

    /// <summary>压紧 left 路由回环边（TB 图，绕簇左方）：竖段从 across 拉到 newX=簇左-边距，连接横段缩短。</summary>
    private static string? CompactLeft(string d, double newX)
    {
        var g = ParseBackEdge(d, horizontal: false);
        if (g is null) return null;
        var (across, n1, n2, r, sx, sy, tx, ty) = g.Value;
        if (r < 2 || r > 25) return null;
        if (Math.Abs(sx - tx) > r + 2) return null;
        var nearSy = Math.Abs(sy - n1) < Math.Abs(sy - n2) ? n1 : n2;
        var nearTy = Math.Abs(sy - n1) < Math.Abs(sy - n2) ? n2 : n1;
        if (Math.Abs(Math.Abs(ty - nearTy) - r) > 3) return null;
        if (newX - across < 8) return null;   // left 路由 across<newX（竖段在左方），须 newX-across≥8
        var newLeftEnd = newX + r;
        if (Math.Min(sx, tx) - newLeftEnd < 4) return null;
        return $"M {F(sx)},{F(sy)} L {F(newLeftEnd)},{F(sy)} Q {F(newX)},{F(sy)} {F(newX)},{F(nearSy)} "
             + $"L {F(newX)},{F(nearTy)} Q {F(newX)},{F(ty)} {F(newLeftEnd)},{F(ty)} L {F(tx)},{F(ty)}";
    }

    /// <summary>数字格式化（4 位小数，去尾零），与 <see cref="StraightenEdges"/> 同口径。</summary>
    private static string F(double v) => v.ToString("0.####", CultureInfo.InvariantCulture);

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

    /// <summary>SVG 字符串 → Avalonia Image（Source = SvgImage 矢量）。经 SvgSource.LoadFromSvg 解析（内部建 SKPicture），
    /// SvgImage（IImage）取矢量渲染，喂标准 Image 控件——Image 按 SvgImage.Size（=CullRect）自动定尺寸，任意 DPI
    /// 清晰、免光栅（旧路 SKSvg→SKBitmap→PNG→Bitmap 已淘汰）。与桌宠 Lottie 同一 SkiaSharp 4.x 渲染栈。
    /// Mermaider BuildOptions(dark) 已给 SVG 设 Bg（白/深），SvgImage 直渲含底色，无需旧 canvas.Clear 兜底。
    /// SvgSource 持原生资源，Image 脱离视觉树时释放防泄漏（主题切换重渲染换掉旧 Image）。</summary>
    private static Image? BuildSvgControl(string svg)
    {
        // SvgSource.LoadFromSvg 返非 null（解析失败抛异常，由 Render 的 try/catch 兜底）。
        var source = SvgSource.LoadFromSvg(svg);
        var picture = source.Picture;
        if (picture is null) { source.Dispose(); return null; }

        var bounds = picture.CullRect;
        if (bounds.Width <= 0 || bounds.Height <= 0) { source.Dispose(); return null; }

        var img = new Image
        {
            Source = new SvgImage { Source = source },
            HorizontalAlignment = HorizontalAlignment.Left,
            Margin = new Thickness(0, 4),
        };
        // 主题切换重渲染会换掉旧 Image → DetachedFromVisualTree 释放 SvgSource（持 SKPicture/SKSvg 原生资源）防泄漏。
        img.DetachedFromVisualTree += (_, _) => source.Dispose();
        return img;
    }
}

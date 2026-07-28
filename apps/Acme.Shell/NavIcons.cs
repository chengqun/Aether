namespace Acme.Shell;

/// <summary>
/// 侧边栏导航图标的矢量 path data（Lucide 图标，MIT；viewBox 24×24，stroke-based）。
/// 用矢量图标替代 emoji，规避跨不同 DPI 显示器时 color font 字形被截断绘制的问题
/// （现象：emoji 右半边被整齐切掉，旁边的文字完好——根因在字形栅格化层，非布局）。
/// 这些字符串绑定到 &lt;Path Data="{Binding Icon}"/&gt;，不经过字体渲染，跨 DPI 一致。
/// </summary>
public static class NavIcons
{
    /// <summary>新聊天（message-square-plus）</summary>
    public const string Chat =
        "M22 17a2 2 0 0 1-2 2H6.828a2 2 0 0 0-1.414.586l-2.202 2.202A.71.71 0 0 1 2 21.286V5a2 2 0 0 1 2-2h16a2 2 0 0 1 2 2z M12 8v6 M9 11h6";

    /// <summary>专家（flask-conical）</summary>
    public const string Experts =
        "M14 2v6a2 2 0 0 0 .245.96l5.51 10.08A2 2 0 0 1 18 22H6a2 2 0 0 1-1.755-2.96l5.51-10.08A2 2 0 0 0 10 8V2 M6.453 15h11.094 M8.5 2h7";

    /// <summary>自动化（clock；circle 转 arc 与指针合并为单段 Geometry）</summary>
    public const string Automation =
        "M12 2 A10 10 0 1 0 12 22 A10 10 0 1 0 12 2 Z M12 6v6l4 2";

    /// <summary>笔记（notebook-pen）</summary>
    public const string Notes =
        "M13.4 2H6a2 2 0 0 0-2 2v16a2 2 0 0 0 2 2h12a2 2 0 0 0 2-2v-7.4 M2 6h4 M2 10h4 M2 14h4 M2 18h4 M21.378 5.626a1 1 0 1 0-3.004-3.004l-5.01 5.012a2 2 0 0 0-.506.854l-.837 2.87a.5.5 0 0 0 .62.62l2.87-.837a2 2 0 0 0 .854-.506z";

    /// <summary>期货（trending-up）</summary>
    public const string Futures =
        "M16 7h6v6 m22 7-8.5 8.5-5-5L2 17";

    /// <summary>回测（chart-column）</summary>
    public const string Backtest =
        "M3 3v16a2 2 0 0 0 2 2h16 M18 17V9 M13 17V5 M8 17v-3";
}

using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Serialization;

namespace Acme.Desktop.Workbench.Abstractions;

/// <summary>布局树节点。声明式、可序列化（JSON）——后续插件/AI 生成只需产出 <see cref="WorkspaceDefinition"/>。</summary>
/// <remarks>
/// 三种节点：
/// <list type="bullet">
/// <item><see cref="LayoutSplit"/> — 子节点按方向分割，相邻子节点之间插可拖 GridSplitter。</item>
/// <item><see cref="LayoutRegion"/> — 单 panel 叶子，对应一个命名 region（向 <see cref="IRegionManager"/> 登记）。</item>
/// <item><see cref="LayoutTabbed"/> — 多 panel 叶子，Grid 叠放无头 tab：所有 panel 视图一次创建常驻，仅选中项 IsVisible
/// （侧栏 nav rail 是唯一切换入口，切换不重建；构建见 WorkspaceView.BuildTabbed）。</item>
/// </list>
/// </remarks>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "$kind")]
[JsonDerivedType(typeof(LayoutSplit), "split")]
[JsonDerivedType(typeof(LayoutRegion), "region")]
[JsonDerivedType(typeof(LayoutTabbed), "tabbed")]
public abstract record LayoutNode
{
    /// <summary>深度优先找布局树里首个 <see cref="LayoutTabbed"/> 节点（导航 rail 据此派生 tab 项）。</summary>
    public static LayoutTabbed? FindFirstTabbed(LayoutNode? node) => node switch
    {
        LayoutTabbed t => t,
        LayoutSplit s => s.Children.Select(FindFirstTabbed).FirstOrDefault(x => x is not null),
        _ => null,
    };
}

/// <summary>分割方向。</summary>
public enum SplitOrientation
{
    Horizontal,
    Vertical
}

/// <summary>分割容器：子节点按 <see cref="Orientation"/> 排列，相邻子节点之间插可拖 GridSplitter。</summary>
/// <param name="Orientation">分割方向。</param>
/// <param name="Children">子布局节点。</param>
/// <param name="Pixels">可选：每个子节点的宽度/高度提示。null = 均分 Star；<paramref name="Pixels"/>[i] &lt;= 0 → Star（弹性）；
/// &gt; 0 → <see cref="Proportional"/>=false 时该子节点固定像素宽/高（如 chat 420px）、=true 时该值作 <b>星权重</b>。长度须等于 Children。</param>
/// <param name="Proportional">true = 比例分摊（2026-10-05「分区按比例分摊」）：子区全部按星权重随窗口缩放同比例伸缩
/// （持久化写权重而非像素；GridSplitter 拖完会把星列改写像素，WorkspaceView 在 DragCompleted 重归一化转回星）。
/// false（默认）= 像素语义照旧——老 JSON 无此键即 false，兼容。</param>
public sealed record LayoutSplit(
    SplitOrientation Orientation,
    IReadOnlyList<LayoutNode> Children,
    double[]? Pixels = null,
    bool Proportional = false) : LayoutNode;

/// <summary>单 panel 叶子：<see cref="RegionId"/> 对应 <see cref="PanelDescriptor.RegionId"/>，同时作为命名 region 登记。</summary>
public sealed record LayoutRegion(string RegionId) : LayoutNode;

/// <summary>多 panel 叶子（TabControl）：<see cref="RegionIds"/> 顺序对应 tab 顺序。</summary>
/// <param name="RegionIds">各 tab 的 region id。</param>
/// <param name="SelectedIndex">初始选中 tab 索引。</param>
public sealed record LayoutTabbed(
    IReadOnlyList<string> RegionIds,
    int SelectedIndex = 0) : LayoutNode;

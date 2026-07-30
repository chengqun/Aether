using CommunityToolkit.Mvvm.ComponentModel;

namespace Acme.Shell;

/// <summary>
/// 笔记节点可选图标项。Key 为持久化标识（存入 NoteNode.Icon）；null 表示「跟随类型」默认。
/// PathData/Color 仅供视图渲染，不持久化。
/// </summary>
public sealed partial class NoteIconOption : ObservableObject
{
    public string Display { get; }
    public string? Key { get; }
    public string PathData { get; }
    public string Color { get; }

    [ObservableProperty] private bool _isSelected;

    public NoteIconOption(string display, string? key, string pathData, string color)
    {
        Display = display;
        Key = key;
        PathData = pathData;
        Color = color;
    }
}

/// <summary>
/// 笔记节点可选图标目录（Lucide 矢量）。key→(path,color) 集中维护；
/// 存储层只保存 key（或 null），改图标库不影响存量数据。
/// </summary>
public static class NoteIcons
{
    /// <summary>Key=null 表示「跟随类型」默认（按 NodeType 回退）。</summary>
    public static readonly NoteIconOption[] Catalog =
    {
        new("默认",   null,      NavIcons.FileText,    "#6B6B70"),
        new("笔记",   "note",    NavIcons.FileText,    "#6B6B70"),
        new("便签",   "sticky",  NavIcons.StickyNote,  "#D97706"),
        new("星标",   "star",    NavIcons.Star,        "#EAB308"),
        new("书签",   "bookmark",NavIcons.Bookmark,    "#5B5FC7"),
        new("机器人", "bot",     NavIcons.Bot,         "#5B5FC7"),
        new("工作流", "workflow",NavIcons.Workflow,    "#1F8A4C"),
        new("代码",   "code",    NavIcons.Code,        "#1F8A4C"),
        new("任务",   "task",    NavIcons.CheckSquare, "#1F8A4C"),
        new("灯泡",   "idea",    NavIcons.Lightbulb,   "#D97706"),
        new("文件夹", "folder",  NavIcons.Folder,      "#6B6B70"),
        new("链接",   "link",    NavIcons.Link,        "#3D40B5"),
        new("心",     "heart",   NavIcons.Heart,       "#EF4444"),
        // —— 风控/数据 场景 ——
        new("查找",   "search",  NavIcons.Search,      "#6B6B70"),  // 特征寻找
        new("变量",   "var",     NavIcons.Cpu,         "#3D40B5"),  // 特征变量
        new("数据",   "data",    NavIcons.Database,    "#6B6B70"),  // 数据加工
        new("过滤",   "filter",  NavIcons.Filter,      "#6B6B70"),
        new("追踪",   "trace",   NavIcons.Target,      "#EF4444"),  // 策略追踪
        new("定位",   "loc",     NavIcons.Crosshair,   "#EF4444"),
        new("路径",   "route",   NavIcons.Route,       "#5B5FC7"),
        new("监控",   "mon",     NavIcons.Activity,    "#0E9488"),
        new("验证",   "verify",  NavIcons.ShieldCheck, "#1F8A4C"),  // 策略验证
        new("标记",   "flag",    NavIcons.Flag,        "#D97706"),
        new("风险",   "risk",    NavIcons.ShieldAlert, "#EF4444"),  // 风险评估
        new("预警",   "alert",   NavIcons.AlertTriangle,"#D97706"),
        new("评估",   "assess",  NavIcons.Gauge,       "#5B5FC7"),
        new("柱图",   "bar",     NavIcons.BarChart,    "#3D40B5"),
        new("饼图",   "pie",     NavIcons.PieChart,    "#0E9488"),
        new("折线",   "line",    NavIcons.LineChart,   "#1F8A4C"),
        new("提醒",   "bell",    NavIcons.Bell,        "#D97706"),
    };

    /// <summary>按 key 查找图标项；key 为 null 或未命中返回 null（调用方回退到 NodeType 默认）。</summary>
    public static NoteIconOption? Find(string? key)
        => string.IsNullOrEmpty(key) ? null : Array.Find(Catalog, o => o.Key == key);
}

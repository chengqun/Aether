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
    /// <summary>true=填充渲染（语言 logo）；false=描边渲染（Lucide）。</summary>
    public bool IsFill { get; }

    [ObservableProperty] private bool _isSelected;

    public NoteIconOption(string display, string? key, string pathData, string color, bool isFill = false)
    {
        Display = display;
        Key = key;
        PathData = pathData;
        Color = color;
        IsFill = isFill;
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
        new("知识",   "knowledge",NavIcons.Knowledge,  "#0891B2"),
        new("规则",   "rule",    NavIcons.Rule,         "#B45309"),
        new("代码",   "code",    NavIcons.Code,        "#1F8A4C"),
        new("任务",   "task",    NavIcons.CheckSquare, "#1F8A4C"),
        new("待办",   "todo",    NavIcons.ListChecks,  "#1F8A4C"),
        new("清单",   "clip2",   NavIcons.ClipboardList,"#6B6B70"),
        new("灯泡",   "idea",    NavIcons.Lightbulb,   "#D97706"),
        new("文件夹", "folder",  NavIcons.Folder,      "#6B6B70"),
        new("目录",   "tree",    NavIcons.FolderTree,  "#6B6B70"),
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
        // —— 通用 ——
        new("大纲",   "outline", NavIcons.ListTree,    "#6B6B70"),
        new("书籍",   "book",    NavIcons.BookOpen,    "#5B5FC7"),
        new("文件夹组","folders",NavIcons.Folders,      "#6B6B70"),
        new("归档",   "archive", NavIcons.Archive,     "#6B6B70"),
        new("日历",   "cal",     NavIcons.Calendar,    "#D97706"),
        new("时钟",   "clock",   NavIcons.Clock,       "#6B6B70"),
        new("密钥",   "key",     NavIcons.Key,         "#D97706"),
        new("锁定",   "lock",    NavIcons.Lock,        "#6B6B70"),
        new("用户",   "user",    NavIcons.User,        "#5B5FC7"),
        new("用户组", "users",   NavIcons.Users,       "#5B5FC7"),
        new("邮件",   "mail",    NavIcons.Mail,        "#3D40B5"),
        new("全球",   "globe",   NavIcons.Globe,       "#0E9488"),
        new("云",     "cloud",   NavIcons.Cloud,       "#6B6B70"),
        new("终端",   "term",    NavIcons.Terminal,    "#1A1A1F"),
        new("文件",   "file",    NavIcons.File,        "#6B6B70"),
        new("删除",   "trash",   NavIcons.Trash,       "#EF4444"),
        new("下载",   "dl",      NavIcons.Download,    "#3D40B5"),
        new("上传",   "ul",      NavIcons.Upload,      "#3D40B5"),
        new("刷新",   "refresh", NavIcons.RefreshCw,   "#6B6B70"),
        new("运行",   "play",    NavIcons.Play,        "#1F8A4C"),
        new("附件",   "clip",    NavIcons.Paperclip,   "#6B6B70"),
        new("勾选",   "check",   NavIcons.Check,       "#1F8A4C"),
        new("关闭",   "close",   NavIcons.Close,       "#EF4444"),
        new("新增",   "plus",    NavIcons.Plus,        "#1F8A4C"),
        // —— 编程语言 logo（Simple Icons 填充式；选对应语法时自动选用）——
        new("Markdown",   "lang_markdown", NavIcons.LangMarkdown,   "#6B6B70", true),
        new("Python",     "lang_python",   NavIcons.LangPython,     "#3776AB", true),
        new("JavaScript", "lang_js",       NavIcons.LangJavascript, "#D97706", true),
        new("JSON",       "lang_json",     NavIcons.LangJson,       "#6B6B70", true),
        new("HTML",       "lang_html",     NavIcons.LangHtml5,      "#E34F26", true),
        new("CSS",        "lang_css",      NavIcons.LangCss3,       "#1572B6", true),
        new("C++",        "lang_cpp",      NavIcons.LangCplusplus,  "#00599C", true),
        new("C#",         "lang_csharp",   NavIcons.LangDotnet,     "#512BD4", true),
        new("Java",       "lang_java",     NavIcons.LangOpenJdk,    "#1A1A1F", true),
        new("VB",         "lang_vb",       NavIcons.LangDotnet,     "#512BD4", true),
        new("XML",        "lang_xml",      NavIcons.LangXml,        "#005FAD", true),
        new("SQL",        "lang_sql",      NavIcons.LangSqlite,     "#003B57", true),
        new("PowerShell", "lang_ps",       NavIcons.LangBash,       "#4EAA25", true),
        new("YAML",       "lang_yaml",     NavIcons.LangYaml,       "#CB171E", true),
        // —— 头像/角色图标 ——
        new("猫",     "cat",     NavIcons.AvatarCat,      "#D97706"),
        new("狗",     "dog",     NavIcons.AvatarDog,      "#8B5E3C"),
        new("鸟",     "bird",    NavIcons.AvatarBird,     "#3D40B5"),
        new("狐狸",   "fox",     NavIcons.AvatarFox,      "#D97706"),
        new("兔子",   "rabbit",  NavIcons.AvatarRabbit,    "#EAB308"),
        new("熊猫",   "panda",   NavIcons.AvatarPanda,    "#1A1A1F"),
        new("猫头鹰", "owl",     NavIcons.AvatarOwl,      "#5B5FC7"),
        new("机器人", "robot",   NavIcons.AvatarRobot,     "#0891B2"),
        new("幽灵",   "ghost",   NavIcons.AvatarGhost,     "#6B6B70"),
        new("雪花",   "snow",    NavIcons.AvatarSnowflake, "#0E9488"),
        new("闪电",   "zap",     NavIcons.AvatarZap,      "#EAB308"),
        new("星球",   "planet",  NavIcons.AvatarPlanet,    "#3D40B5"),
        new("蘑菇",   "mushroom",NavIcons.AvatarMushroom,  "#EF4444"),
        new("花朵",   "flower",  NavIcons.AvatarFlower,    "#EF4444"),
        new("月亮",   "moon",    NavIcons.AvatarMoon,      "#5B5FC7"),
        new("太阳",   "sun",     NavIcons.AvatarSun,      "#D97706"),
        new("钻石",   "gem",     NavIcons.AvatarGem,       "#0E9488"),
        new("火焰",   "flame",   NavIcons.AvatarFlame,     "#EF4444"),
        new("海豚",   "dolphin", NavIcons.AvatarDolphin,   "#0891B2"),
    };

    /// <summary>语法 key → 对应图标 key（用于选语法时自动设节点图标）。</summary>
    public static string? IconKeyForSyntax(string? syntax) => syntax?.ToLowerInvariant() switch
    {
        "markdown" => "lang_markdown",
        "python" => "lang_python",
        "javascript" => "lang_js",
        "json" => "lang_json",
        "html" => "lang_html",
        "css" => "lang_css",
        "cpp" => "lang_cpp",
        "csharp" => "lang_csharp",
        "java" => "lang_java",
        "vb" => "lang_vb",
        "xml" => "lang_xml",
        "sql" => "lang_sql",
        "powershell" => "lang_ps",
        "yaml" => "lang_yaml",
        "text" => "file",
        "folder" => "folder",
        _ => null,
    };

    /// <summary>按 key 查找图标项；key 为 null 或未命中返回 null（调用方回退到 NodeType 默认）。</summary>
    public static NoteIconOption? Find(string? key)
        => string.IsNullOrEmpty(key) ? null : Array.Find(Catalog, o => o.Key == key);
}

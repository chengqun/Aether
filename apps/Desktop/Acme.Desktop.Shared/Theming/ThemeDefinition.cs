using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace Acme.Desktop.Shared.Theming;

/// <summary>JSON 主题 DTO。用户自定义主题：颜色/字体/圆角/间距/背景。经 <see cref="ThemeLoader"/> 反序列化成运行时 ResourceDictionary。</summary>
public sealed class ThemeDefinition
{
    /// <summary>颜色资源：key → 值。以 "Brush" 结尾 → SolidColorBrush；以 "Color" 结尾 → Color（并自动派生同名 Brush）。
    /// 覆盖 SharedTheme/WorkbenchTheme 的 AccentBrush/SidebarBgBrush/ContentBgBrush/BorderBrush/TextPrimaryBrush 等。</summary>
    [JsonPropertyName("colors")]
    public Dictionary<string, string> Colors { get; set; } = new();

    /// <summary>渐变画刷覆盖：key → <see cref="GradientSpec"/>。key 须为 *Brush 资源（AccentBrush/WorkspaceBackground/SidebarBgBrush/CardBgBrush…）。
    /// 在 <see cref="Colors"/> 的纯色 Brush 之上覆盖为渐变——给"炫"主题用（霓虹/梦幻渐变主色）。纯色主题留空。
    /// 注意：<c>WorkspaceBackground</c> 由 <see cref="Background"/> 段权威驱动，此处对它覆盖会被 Background 段重写——背景渐变请走 <see cref="BackgroundDef.Gradient"/>。</summary>
    [JsonPropertyName("gradients")]
    public Dictionary<string, GradientSpec>? Gradients { get; set; }

    /// <summary>字体资源：key → FontFamily 串。如 "UiFont"/"MonoFont"/"CodeFont"。</summary>
    [JsonPropertyName("fonts")]
    public Dictionary<string, string> Fonts { get; set; } = new();

    /// <summary>圆角 token：key → 数值串。如 "CornerRadiusMd"。</summary>
    [JsonPropertyName("radius")]
    public Dictionary<string, string> Radius { get; set; } = new();

    /// <summary>间距 token：key → 数值串。如 "SpaceMd"。</summary>
    [JsonPropertyName("spacing")]
    public Dictionary<string, string> Spacing { get; set; } = new();

    /// <summary>背景资源（WorkspaceBackground）：渐变 / 图片 asset / 纯色 fallback（按此优先级）。</summary>
    [JsonPropertyName("background")]
    public BackgroundDef? Background { get; set; }

    /// <summary>主题明暗倾向："light" / "dark"（可选）。决定 Avalonia 原生控件（TextBox/ComboBox/滚动条…）
    /// 的 <see cref="Avalonia.Styling.ThemeVariant"/>——JSON overlay 只覆盖自定义 token 画刷，
    /// 原生控件须经 ThemeVariant 翻转，否则 dark 主题下原生控件仍是浅底白块。缺省按 "dark" 处理。</summary>
    [JsonPropertyName("variant")]
    public string? Variant { get; set; }

    /// <summary>是否启用动画梦幻背景层（aurora 漂移光斑）。dream 类皮肤置 true；普通主题缺省/=false = 静态。
    /// 驱动 WorkbenchWindow 的 DreamLayer——半透明面板（alpha hex）让 aurora 透出，无面板需半透明时此层被遮挡。</summary>
    [JsonPropertyName("animated")]
    public bool? Animated { get; set; }

    /// <summary>皮肤 .axaml 样式包 URI（avares:// 或文件路径）。指向一个 &lt;Styles&gt; 根的编译 .axaml，
    /// 经 <see cref="ThemeLoader"/> 用 StyleInclude 载入并 merge 进目标元素的 <c>Styles</c>——
    /// Codex-CSS 式"换肤控制模板/特效"：发光(DropShadowEffect)/玻璃(acrylic)/自定义控件模板/悬停动画，
    /// 这些非 token 化的东西放 .axaml 而非 JSON。token overlay（颜色/渐变）+ styles（特效/模板）= 完整皮肤包。
    /// 缺省/=null = 纯 token 皮肤（light/dark 即此）。</summary>
    [JsonPropertyName("styles")]
    public string? Styles { get; set; }

    /// <summary>窗口 chrome 定义（边框/标题栏）。dream 类皮肤置 borderless=true 实现无边框发光窗
    /// （aurora/渐变底延伸到屏幕边缘 + 自绘标题栏，VSCode/Codex 式沉浸感）。缺省/=null = 系统原生 chrome
    /// （light/dark 即此——保留 OS 标题栏/交通灯按钮）。只对 WorkbenchWindow 生效；子窗（ThemedWindow）不变。</summary>
    [JsonPropertyName("chrome")]
    public ChromeDef? Chrome { get; set; }

    /// <summary>编辑器（AvaloniaEdit）配色：语法着色 token + 外观色（当前行/选区/底）。
    /// 缺省/=null = 回落到 CodeEditorSetup 内置 VSCode 2026 双配色（浅/深）。提供了即按语义槽覆盖——
    /// 编辑器是进程级全局（HighlightingManager 缓存 + CodeEditorSetup 静态），故此段由 ThemeLoader 写入
    /// 全局静态槽（非 per-window ResourceDictionary），供 CodeEditorSetup 查询。</summary>
    [JsonPropertyName("editor")]
    public EditorThemeDef? Editor { get; set; }
}

/// <summary>编辑器配色定义。每个槽 = hex 色（#RRGGBB / #AARRGGBB）。未提供的槽缺省回落 VSCode 2026 内置色。
/// 语义槽分两层：语法高亮（keyword/string/number/comment/function/type/tag/attribute/heading/link）+ 外观
/// （background=编辑器底 / currentLine=当前行底 / selection=选区底 / selectionForeground=选区文字）。</summary>
public sealed class EditorThemeDef
{
    [JsonPropertyName("keyword")] public string? Keyword { get; set; }
    [JsonPropertyName("string")] public string? String { get; set; }
    [JsonPropertyName("number")] public string? Number { get; set; }
    [JsonPropertyName("comment")] public string? Comment { get; set; }
    [JsonPropertyName("function")] public string? Function { get; set; }
    [JsonPropertyName("type")] public string? Type { get; set; }
    [JsonPropertyName("tag")] public string? Tag { get; set; }
    [JsonPropertyName("attribute")] public string? Attribute { get; set; }
    [JsonPropertyName("heading")] public string? Heading { get; set; }
    [JsonPropertyName("link")] public string? Link { get; set; }

    [JsonPropertyName("background")] public string? Background { get; set; }
    [JsonPropertyName("currentLine")] public string? CurrentLine { get; set; }
    [JsonPropertyName("selection")] public string? Selection { get; set; }
    [JsonPropertyName("selectionForeground")] public string? SelectionForeground { get; set; }
}

/// <summary>窗口 chrome 定义。当前仅 borderless 开关 + 标题栏高度；后续可扩（按钮位/圆角等）。</summary>
public sealed class ChromeDef
{
    /// <summary>true = 无边框自绘标题栏（ExtendClientAreaToDecorationsHint + NoChrome + 自绘标题栏/按钮）。
    /// null/false = 系统原生 chrome。仅 dream 类炫皮肤置 true。</summary>
    [JsonPropertyName("borderless")]
    public bool? Borderless { get; set; }

    /// <summary>自绘标题栏高度（px）。borderless=true 时生效，默认 36。决定顶部拖拽区/窗口按钮带高度。</summary>
    [JsonPropertyName("titleBarHeight")]
    public double? TitleBarHeight { get; set; }
}

/// <summary>背景定义。优先级：<see cref="Gradient"/> &gt; <see cref="Image"/> &gt; <see cref="Color"/>。</summary>
public sealed class BackgroundDef
{
    /// <summary>渐变背景（优先）。给"梦幻"渐变底用——无需图片 asset。</summary>
    [JsonPropertyName("gradient")]
    public GradientSpec? Gradient { get; set; }

    /// <summary>图片 asset 路径（avares:// 或文件路径）。优先于 <see cref="Color"/>。</summary>
    [JsonPropertyName("image")]
    public string? Image { get; set; }

    /// <summary>纯色 fallback（hex）。</summary>
    [JsonPropertyName("color")]
    public string? Color { get; set; }

    /// <summary>拉伸模式：None/Fill/Uniform/UniformToFill（默认 UniformToFill）。仅对 image 生效。</summary>
    [JsonPropertyName("stretch")]
    public string? Stretch { get; set; }
}

/// <summary>渐变画刷规格。覆盖同名 *Brush 资源为线性渐变（梦幻/霓虹）。</summary>
public sealed class GradientSpec
{
    /// <summary>渐变类型。当前实现 "linear"（默认）；"radial" 预留（P1 暂回落 linear）。</summary>
    [JsonPropertyName("type")]
    public string? Type { get; set; }

    /// <summary>线性渐变角度（度；0=自下向上，顺时针，同 CSS linear-gradient）。默认 135（左上→右下）。</summary>
    [JsonPropertyName("angle")]
    public double? Angle { get; set; }

    /// <summary>渐变停靠点（≥2，按 offset 升序）。首色在起点，末色在终点。</summary>
    [JsonPropertyName("stops")]
    public List<GradientStopSpec> Stops { get; set; } = new();
}

/// <summary>渐变停靠点。</summary>
public sealed class GradientStopSpec
{
    /// <summary>hex 色（如 "#5B5FC7"，支持 8 位 #RRGGBBAA）。</summary>
    [JsonPropertyName("color")]
    public string? Color { get; set; }

    /// <summary>位置 0.0..1.0。</summary>
    [JsonPropertyName("offset")]
    public double? Offset { get; set; }
}

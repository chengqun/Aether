using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Acme.Desktop.Mvvm;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Acme.Desktop.Mvvm.Palette;

/// <summary>主题编辑器 ViewModel：4 tab（颜色/字体/圆角与间距/明暗基底），全量编辑主题 token。
/// 颜色 tab 主从式——左 35 色项列表带色块预览，选中→右常驻 ColorView 改该色；主色联动重算派生族。
/// 任何改动经 <see cref="IThemeHost.SetCustomThemeJson"/> 整窗实时预览；保存经
/// <see cref="IThemeHost.SaveCustomThemeAsync"/> 落盘成命名主题。UI-agnostic（零 Avalonia）：
/// 颜色用 hex 字符串、数字用 double、字体用 FontFamily 串；派生算法纯函数可单测。
/// ColorView↔Hex 经 Shared 层 HexToColorConverter 桥接。</summary>
public sealed partial class PaletteViewModel : ObservableObject
{
    private readonly IThemeHost _host;
    private readonly string _originalJson;   // 打开时的 overlay JSON（取消恢复用）
    private bool _loading = true;            // 初始化填值时不触发预览
    private bool _syncing;                   // 主色联动设派生 Hex 时抑制 RebuildPreview 循环

    /// <summary>颜色 tab：35 色 token 可编辑项（主从式左列表）。WorkspaceBackground 归明暗基底 tab 管，不在此列。</summary>
    public ObservableCollection<PaletteColorRow> ColorRows { get; }

    /// <summary>字体 tab：3 个 FontFamily 串。</summary>
    public ObservableCollection<FontRow> FontRows { get; }

    /// <summary>圆角与间距 tab：3 radius + 5 spacing（全量；BuildOverlayJson 按 Category 路由）。</summary>
    public ObservableCollection<NumberRow> NumberRows { get; }

    /// <summary>圆角与间距 tab「圆角」分区视图（共享 NumberRow 实例，编辑即同步）。</summary>
    public IReadOnlyList<NumberRow> RadiusRows { get; }

    /// <summary>圆角与间距 tab「间距」分区视图（共享 NumberRow 实例，编辑即同步）。</summary>
    public IReadOnlyList<NumberRow> SpacingRows { get; }

    /// <summary>颜色 tab 当前选中项（右 ColorView 绑此 Hex）。初始选主色项。</summary>
    [ObservableProperty]
    private PaletteColorRow? _selectedColorRow;

    /// <summary>明暗基底 tab：窗口背景色（写 background.color，ThemeLoader 覆盖 WorkspaceBackground）。</summary>
    [ObservableProperty]
    private PaletteColorRow _backgroundColor = new("窗口背景", "background.color", "背景");

    /// <summary>主题名（保存成 themes/&lt;名&gt;.json）。</summary>
    [ObservableProperty]
    private string _themeName = "我的主题";

    /// <summary>明暗基底（light/dark）——驱动 native 控件 ThemeVariant（host impl 解析同步 IsDarkVariant）。</summary>
    [ObservableProperty]
    private string _variant = "light";

    /// <summary>保存/取消命令执行完毕触发；PaletteDialogService 订阅以关闭窗口。</summary>
    public event Action? Finished;

    public PaletteViewModel(IThemeHost host)
    {
        _host = host;
        _originalJson = host.CustomThemeJson ?? "";

        var colors = ParseColors(_originalJson);
        var fonts = ParseSection(_originalJson, "fonts");
        var radius = ParseSection(_originalJson, "radius");
        var spacing = ParseSection(_originalJson, "spacing");

        ColorRows = new(BuildColorRows(colors));
        FontRows = new(BuildFontRows(fonts));
        NumberRows = new(BuildNumberRows(radius, spacing));
        RadiusRows = NumberRows.Where(n => n.Category == "圆角").ToList();
        SpacingRows = NumberRows.Where(n => n.Category == "间距").ToList();
        BackgroundColor.Hex = ParseBackgroundColor(_originalJson);
        Variant = ParseVariant(_originalJson);

        foreach (var r in ColorRows)
            r.PropertyChanged += OnColorRowChanged;
        BackgroundColor.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(PaletteColorRow.Hex) && !_loading) RebuildPreview();
        };
        foreach (var f in FontRows)
            f.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(FontRow.Value) && !_loading) RebuildPreview();
            };
        foreach (var n in NumberRows)
            n.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(NumberRow.Value) && !_loading) RebuildPreview();
            };

        SelectedColorRow = ColorRows.FirstOrDefault(r => r.IsAccent) ?? ColorRows.FirstOrDefault();
        _loading = false;
    }

    private void OnColorRowChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(PaletteColorRow.Hex) || _loading || _syncing) return;
        if (sender is PaletteColorRow r && r.IsAccent)
            SyncDerivedRows(r.Hex);   // 主色变 → 重算派生项（_syncing 抑制它们的 PropertyChanged）
        RebuildPreview();
    }

    /// <summary>主色联动：重算派生项 Hex。_syncing=true 期间派生项 PropertyChanged 被跳过，避免循环 RebuildPreview。</summary>
    private void SyncDerivedRows(string accent)
    {
        _syncing = true;
        try
        {
            var d = DeriveAccentFamily(accent);
            foreach (var row in ColorRows.Where(r => r.IsDerived))
                if (d.TryGetValue(row.Key, out var v)) row.Hex = v;
        }
        finally { _syncing = false; }
    }

    /// <summary>重建 overlay JSON 并设到 host 触发整窗实时预览。</summary>
    private void RebuildPreview() => _host.SetCustomThemeJson(BuildOverlayJson());

    /// <summary>variant 变（明暗基底 RadioButton）→ 实时预览（host 解析 variant 同步 IsDarkVariant 翻 native 控件）。</summary>
    partial void OnVariantChanged(string value)
    {
        if (!_loading) RebuildPreview();
    }

    [RelayCommand]
    private async Task SaveAsync()
    {
        var name = string.IsNullOrWhiteSpace(ThemeName) ? "我的主题" : ThemeName.Trim();
        await _host.SaveCustomThemeAsync(name, BuildOverlayJson());
        Finished?.Invoke();
    }

    [RelayCommand]
    private void Cancel()
    {
        // 恢复打开编辑器前的 overlay（null = 基线无 overlay）。
        _host.SetCustomThemeJson(string.IsNullOrEmpty(_originalJson) ? null : _originalJson);
        Finished?.Invoke();
    }

    // ── 35 色 ColorRow 定义：标签 / token key / 分组 / 是否主色入口 / 是否派生族（只读）──
    private static readonly (string Label, string Key, string Category, bool Accent, bool Derived)[] ColorDefs =
    {
        // Accent 族（主色 + 4 派生；AccentBrush/SidebarActive/UserBubble 见各自组，=主色派生）
        ("主色", "AccentColor", "Accent", true, false),
        ("强调画刷", "AccentBrush", "Accent", false, true),
        ("悬停色", "AccentHoverBrush", "Accent", false, true),
        ("浅强调", "AccentLightBrush", "Accent", false, true),
        ("淡底色", "AccentTintBgBrush", "Accent", false, true),
        // Sidebar 族
        ("侧栏背景", "SidebarBgBrush", "Sidebar", false, false),
        ("侧栏悬停", "SidebarHoverBrush", "Sidebar", false, false),
        ("侧栏选中", "SidebarActiveBrush", "Sidebar", false, true),
        ("侧栏图标", "SidebarIconBrush", "Sidebar", false, false),
        ("侧栏文字", "SidebarTextBrush", "Sidebar", false, false),
        // 容器（WorkspaceBackground 归明暗基底 tab，不在此）
        ("内容背景", "ContentBgBrush", "容器", false, false),
        ("卡片背景", "CardBgBrush", "容器", false, false),
        ("边框", "BorderBrush", "容器", false, false),
        ("表面", "SurfaceBrush", "容器", false, false),
        ("表面悬停", "SurfaceHoverBrush", "容器", false, false),
        ("导航悬停", "NavHoverBrush", "容器", false, false),
        // 文本
        ("主文本", "TextPrimaryBrush", "文本", false, false),
        ("次文本", "TextSecondaryBrush", "文本", false, false),
        ("强调上文字", "OnAccentColor", "文本", false, false),
        ("危险上文字", "OnDangerColor", "文本", false, false),
        // 状态
        ("成功", "SuccessBrush", "状态", false, false),
        ("成功底", "SuccessBgBrush", "状态", false, false),
        ("危险色", "DangerColor", "状态", false, false),
        ("危险", "DangerBrush", "状态", false, false),
        ("危险底", "DangerBgBrush", "状态", false, false),
        ("警告", "WarningBrush", "状态", false, false),
        ("警告底", "WarningBgBrush", "状态", false, false),
        // 气泡
        ("用户气泡", "UserBubbleBrush", "气泡", false, true),
        ("Agent 气泡", "AgentBubbleBrush", "气泡", false, false),
        // 工具卡
        ("工具调用", "ToolCallBrush", "工具卡", false, false),
        ("调用边框", "ToolCallBorderBrush", "工具卡", false, false),
        ("调用文字", "ToolCallTextBrush", "工具卡", false, false),
        ("工具结果", "ToolResultBrush", "工具卡", false, false),
        ("结果边框", "ToolResultBorderBrush", "工具卡", false, false),
        ("结果文字", "ToolResultTextBrush", "工具卡", false, false),
    };

    private static List<PaletteColorRow> BuildColorRows(Dictionary<string, string> colors)
    {
        var rows = new List<PaletteColorRow>();
        foreach (var (label, key, cat, accent, derived) in ColorDefs)
        {
            var hex = colors.TryGetValue(key, out var v) && !string.IsNullOrEmpty(v)
                ? v
                : (accent ? "#5B5FC7" : "#FFFFFF");
            rows.Add(new PaletteColorRow(label, key, cat, accent, derived) { Hex = hex });
        }
        return rows;
    }

    private static readonly (string Label, string Key)[] FontDefs =
    {
        ("UI 字体", "UiFont"),
        ("等宽字体", "MonoFont"),
        ("代码字体", "CodeFont"),
    };

    private static List<FontRow> BuildFontRows(JsonObject? fonts)
    {
        var rows = new List<FontRow>();
        foreach (var (label, key) in FontDefs)
        {
            var v = fonts is not null && fonts.TryGetPropertyValue(key, out var n) ? n?.GetValue<string>() ?? "" : "";
            rows.Add(new FontRow(label, key, v));
        }
        return rows;
    }

    private static readonly (string Label, string Key, string Cat, double Def, double Min, double Max, double Step)[] NumberDefs =
    {
        ("圆角-小", "CornerRadiusSm", "圆角", 4, 0, 32, 1),
        ("圆角-中", "CornerRadiusMd", "圆角", 8, 0, 32, 1),
        ("圆角-大", "CornerRadiusLg", "圆角", 12, 0, 32, 1),
        ("间距-Xs", "SpaceXs", "间距", 4, 0, 48, 2),
        ("间距-Sm", "SpaceSm", "间距", 8, 0, 48, 2),
        ("间距-Md", "SpaceMd", "间距", 12, 0, 48, 2),
        ("间距-Lg", "SpaceLg", "间距", 16, 0, 48, 2),
        ("间距-Xl", "SpaceXl", "间距", 24, 0, 48, 2),
    };

    private static List<NumberRow> BuildNumberRows(JsonObject? radius, JsonObject? spacing)
    {
        var rows = new List<NumberRow>();
        foreach (var (label, key, cat, def, min, max, step) in NumberDefs)
        {
            var src = cat == "圆角" ? radius : spacing;
            var v = def;
            if (src is not null && src.TryGetPropertyValue(key, out var n) && n is not null)
            {
                var raw = n.GetValue<string>();
                if (double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var d))
                    v = d;
            }
            rows.Add(new NumberRow(label, key, cat, v, min, max, step));
        }
        return rows;
    }

    /// <summary>深拷贝当前主题 JSON（保留 $note + 未调 token），覆盖五类 token + variant + background.color。</summary>
    private string BuildOverlayJson()
    {
        JsonNode root = string.IsNullOrWhiteSpace(_originalJson)
            ? new JsonObject()
            : JsonNode.Parse(_originalJson) ?? new JsonObject();
        var obj = root.AsObject();

        // colors：派生族（7，含主色）+ 独立色（非主色非派生）
        var colors = obj.TryGetPropertyValue("colors", out var c) && c is JsonObject co ? co : new JsonObject();
        obj["colors"] = colors;
        var accent = HexOf("AccentColor");
        foreach (var (k, v) in DeriveAccentFamily(accent))
            colors[k] = v;
        foreach (var r in ColorRows)
            if (!r.IsAccent && !r.IsDerived)
                colors[r.Key] = r.Hex;

        // fonts
        var fonts = obj.TryGetPropertyValue("fonts", out var f) && f is JsonObject fo ? fo : new JsonObject();
        obj["fonts"] = fonts;
        foreach (var fr in FontRows)
            fonts[fr.Key] = fr.Value;

        // radius + spacing
        var radius = obj.TryGetPropertyValue("radius", out var r2) && r2 is JsonObject ro ? ro : new JsonObject();
        obj["radius"] = radius;
        var spacing = obj.TryGetPropertyValue("spacing", out var s) && s is JsonObject so ? so : new JsonObject();
        obj["spacing"] = spacing;
        foreach (var nr in NumberRows)
        {
            var val = nr.Value.ToString();
            if (nr.Category == "圆角") radius[nr.Key] = val;
            else spacing[nr.Key] = val;
        }

        // variant
        obj["variant"] = Variant;

        // background.color
        var bg = obj.TryGetPropertyValue("background", out var b) && b is JsonObject bo ? bo : new JsonObject();
        obj["background"] = bg;
        bg["color"] = BackgroundColor.Hex;

        return obj.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
    }

    private string HexOf(string key) => ColorRows.FirstOrDefault(r => r.Key == key)?.Hex ?? "#FFFFFF";

    // ── JSON 解析 helpers ──

    /// <summary>解析主题 JSON 的某节（fonts/radius/spacing）为 JsonObject。</summary>
    private static JsonObject? ParseSection(string json, string name)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            if (JsonNode.Parse(json)?.AsObject() is { } root
                && root.TryGetPropertyValue(name, out var n) && n is JsonObject o)
                return o;
        }
        catch { /* 非法 JSON → null，编辑器用默认值 */ }
        return null;
    }

    private static string ParseVariant(string json)
    {
        if (string.IsNullOrWhiteSpace(json)) return "light";
        try
        {
            if (JsonNode.Parse(json)?.AsObject() is { } root
                && root.TryGetPropertyValue("variant", out var n) && n is JsonValue v)
                return v.GetValue<string>();
        }
        catch { }
        return "light";
    }

    private static string ParseBackgroundColor(string json)
    {
        if (string.IsNullOrWhiteSpace(json)) return "#FFFFFF";
        try
        {
            if (JsonNode.Parse(json)?.AsObject() is { } root
                && root.TryGetPropertyValue("background", out var b) && b is JsonObject bo
                && bo.TryGetPropertyValue("color", out var c))
                return c?.GetValue<string>() ?? "#FFFFFF";
        }
        catch { }
        return "#FFFFFF";
    }

    /// <summary>解析主题 JSON 的 colors 字段为 token→hex 字典。</summary>
    private static Dictionary<string, string> ParseColors(string json)
    {
        var d = new Dictionary<string, string>();
        if (string.IsNullOrWhiteSpace(json)) return d;
        try
        {
            if (JsonNode.Parse(json)?.AsObject() is { } root
                && root.TryGetPropertyValue("colors", out var c) && c is JsonObject co)
            {
                foreach (var p in co)
                    d[p.Key] = p.Value?.GetValue<string>() ?? "";
            }
        }
        catch { /* 非法 JSON → 空表，编辑器用默认色 */ }
        return d;
    }

    // ── 派生算法（纯 hex 字符串，零 Avalonia 依赖，可单测）──

    /// <summary>主色派生族：调 accent 时自动重算 hover/light/tint + accent 同值族。</summary>
    public static Dictionary<string, string> DeriveAccentFamily(string accentHex) => new()
    {
        ["AccentColor"] = accentHex,
        ["AccentBrush"] = accentHex,
        ["AccentHoverBrush"] = MixHex(accentHex, "#000000", 0.15),   // 变暗 15%
        ["AccentLightBrush"] = MixHex(accentHex, "#FFFFFF", 0.25),   // 变亮 25%
        ["AccentTintBgBrush"] = MixHex(accentHex, "#FFFFFF", 0.88),  // 极淡底色
        ["SidebarActiveBrush"] = accentHex,
        ["UserBubbleBrush"] = accentHex,
    };

    /// <summary>RGB 线性混合 a*(1-t)+b*t，返回 #RRGGBB。解析失败回退 a。</summary>
    public static string MixHex(string aHex, string bHex, double t)
    {
        if (!TryParseHex(aHex, out var a) || !TryParseHex(bHex, out var b)) return aHex;
        var r = ClampByte(a.r * (1 - t) + b.r * t);
        var g = ClampByte(a.g * (1 - t) + b.g * t);
        var b2 = ClampByte(a.b * (1 - t) + b.b * t);
        return $"#{r:X2}{g:X2}{b2:X2}";
    }

    private static byte ClampByte(double v) => (byte)Math.Clamp(Math.Round(v), 0, 255);

    private static bool TryParseHex(string? hex, out (int r, int g, int b) rgb)
    {
        rgb = default;
        if (string.IsNullOrEmpty(hex)) return false;
        var h = hex.Trim();
        if (h.StartsWith('#')) h = h[1..];
        if (h.Length == 8) h = h[0..6];      // #RRGGBBAA → 丢末尾 AA（CSS/Web 惯例，保留 RRGGBB）
        if (h.Length != 6) return false;
        if (!TryHexByte(h[0..2], out var r)) return false;
        if (!TryHexByte(h[2..4], out var g)) return false;
        if (!TryHexByte(h[4..6], out var b)) return false;
        rgb = (r, g, b);
        return true;
    }

    private static bool TryHexByte(string s, out int v) =>
        int.TryParse(s, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out v);
}

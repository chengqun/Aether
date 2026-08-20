using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Styling;

namespace Acme.Desktop.Workbench.Theming;

/// <summary>
/// JSON 主题加载器：ThemeDefinition（JSON）→ 运行时 ResourceDictionary（Color/SolidColorBrush/LinearGradientBrush/FontFamily/CornerRadius/Thickness/ImageBrush）。
/// 叠加在 WorkbenchTheme.axaml（编译期 token 基线）之上。内置主题（light/dark）
/// 同为 JSON overlay——换 JSON 即换肤，无需重编；用户可在 &lt;tenantDir&gt;/themes/*.json 放自定义主题，或同名 JSON 覆盖内置。
/// 全部经 {DynamicResource} 消费 → overlay merge 后全量重染（颜色/字体/圆角/间距/背景）。
/// </summary>
public static class ThemeLoader
{
    private static readonly JsonSerializerOptions _jsonOpts = new() { PropertyNameCaseInsensitive = true };

    private static readonly ConditionalWeakTable<StyledElement, ResourceDictionary> _overlays = new();

    /// <summary>每个目标元素当前应用的皮肤 .axaml 样式包（StyleInclude），按目标跟踪——换肤时先移除旧样式再装新。
    /// 与 _overlays（token ResourceDictionary）平级：token overlay 覆盖颜色/渐变画刷，styles overlay 覆盖特效/控制模板。</summary>
    private static readonly ConditionalWeakTable<StyledElement, IStyle> _styleOverlays = new();

    /// <summary>内置主题清单（name→显示描述）。每个 name 对应内嵌资源 Assets/{name}.json。
    /// 新增内置主题 = 把 JSON 放进 Assets/ + 在此加一行（csproj 的 Assets\**\* glob 自动纳入资源）。</summary>
    public static readonly (string Name, string Description)[] BuiltInThemes =
    {
        ("light", "内置浅色"),
        ("dark", "内置深色"),
        ("dream", "梦幻渐变"),
    };

    /// <summary>判断 name 是否为内置主题名。</summary>
    public static bool IsBuiltIn(string name)
        => Array.Exists(BuiltInThemes, t => t.Name == name);

    /// <summary>反序列化 JSON。失败返回 null。</summary>
    public static ThemeDefinition? Deserialize(string json)
    {
        try { return JsonSerializer.Deserialize<ThemeDefinition>(json, _jsonOpts); }
        catch { return null; }
    }

    /// <summary>读内嵌主题 JSON（Assets/{name}.json）为字符串。资源不存在返 null。</summary>
    public static string? GetBuiltInJson(string name)
    {
        var uri = new Uri($"avares://Acme.Desktop.Workbench/Assets/{name}.json");
        if (!AssetLoader.Exists(uri)) return null;
        using var s = AssetLoader.Open(uri);
        using var r = new StreamReader(s);
        return r.ReadToEnd();
    }

    /// <summary>读内嵌 dark 主题（Assets/dark.json，内置深色）。委托 <see cref="GetBuiltInJson"/>。</summary>
    public static string? GetDarkJson() => GetBuiltInJson("dark");

    /// <summary>读内嵌 light 主题（Assets/light.json）。委托 <see cref="GetBuiltInJson"/>。
    /// 内置 light 也是 JSON overlay（与 dark 对称），值与 SharedTheme.axaml 基线一致——显式化使其可被用户 themes/light.json 覆盖。</summary>
    public static string? GetLightJson() => GetBuiltInJson("light");

    /// <summary>把定义转成 ResourceDictionary（每种 token 转成对应 Avalonia 资源类型）。</summary>
    public static ResourceDictionary ToResourceDictionary(ThemeDefinition def)
    {
        var dict = new ResourceDictionary();
        if (def.Colors is not null)
        {
            foreach (var (key, val) in def.Colors)
            {
                if (!Color.TryParse(val, out var color)) continue;
                if (key.EndsWith("Brush", StringComparison.Ordinal))
                {
                    dict[key] = new SolidColorBrush(color);
                    // Brush 键反向派生 Color（AccentBrush → AccentColor），让直接消费 Color 资源的 XAML 也跟随主题。
                    var colorKey = key[..^"Brush".Length] + "Color";
                    if (!def.Colors.ContainsKey(colorKey))
                        dict[colorKey] = color;
                }
                else
                {
                    dict[key] = color;
                    // Color 键自动派生 Brush（AccentColor → AccentBrush），方便覆盖 SharedTheme 画刷。
                    if (key.EndsWith("Color", StringComparison.Ordinal))
                    {
                        var brushKey = key[..^"Color".Length] + "Brush";
                        if (!def.Colors.ContainsKey(brushKey))
                            dict[brushKey] = new SolidColorBrush(color);
                    }
                }
            }
        }
        // 渐变画刷覆盖：在 Colors 纯色 Brush 之上覆盖为渐变（LinearGradientBrush）。给"炫"主题用（梦幻/霓虹渐变主色）。
        if (def.Gradients is { Count: > 0 })
        {
            foreach (var (key, spec) in def.Gradients)
            {
                var g = BuildGradient(spec);
                if (g is null) continue;
                dict[key] = g;
                // *Brush 渐变 → *Color 取首停靠点色（让消费 Color 资源的 XAML 不崩；colors 里显式给了则已覆盖，此处仅在缺时补）
                if (key.EndsWith("Brush", StringComparison.Ordinal))
                {
                    var colorKey = key[..^"Brush".Length] + "Color";
                    if (def.Colors?.ContainsKey(colorKey) != true)
                    {
                        foreach (var s in spec.Stops)
                        {
                            if (!string.IsNullOrWhiteSpace(s.Color) && Color.TryParse(s.Color!, out var sc))
                            {
                                dict[colorKey] = sc;
                                break;
                            }
                        }
                    }
                }
            }
        }
        if (def.Fonts is not null)
        {
            foreach (var (key, val) in def.Fonts)
            {
                try { dict[key] = FontFamily.Parse(val); }
                catch { /* 忽略非法字体串 */ }
            }
        }
        if (def.Radius is not null)
        {
            foreach (var (key, val) in def.Radius)
            {
                if (double.TryParse(val, out var d))
                    dict[key] = new CornerRadius(d);
            }
        }
        if (def.Spacing is not null)
        {
            foreach (var (key, val) in def.Spacing)
            {
                try { dict[key] = Thickness.Parse(val); }
                catch { /* 忽略 */ }
            }
        }
        if (def.Background is not null)
        {
            var bg = BuildBackground(def.Background);
            if (bg is not null)
                dict["WorkspaceBackground"] = bg;
        }
        return dict;
    }

    private static IBrush? BuildBackground(BackgroundDef bg)
    {
        // 优先级：渐变 > 图片 > 纯色
        if (bg.Gradient is not null)
        {
            var g = BuildGradient(bg.Gradient);
            if (g is not null) return g;
        }
        if (!string.IsNullOrWhiteSpace(bg.Image))
        {
            try
            {
                var uri = new Uri(bg.Image, UriKind.RelativeOrAbsolute);
                if (AssetLoader.Exists(uri))
                {
                    using var s = AssetLoader.Open(uri);
                    var bmp = new Bitmap(s);
                    var stretch = bg.Stretch is { } st && Enum.TryParse<Stretch>(st, true, out var parsed)
                        ? parsed : Stretch.UniformToFill;
                    return new ImageBrush(bmp) { Stretch = stretch };
                }
            }
            catch { /* 图片加载失败 → fallback 纯色 */ }
        }
        if (!string.IsNullOrWhiteSpace(bg.Color) && Color.TryParse(bg.Color, out var color))
            return new SolidColorBrush(color);
        return null;
    }

    /// <summary>GradientSpec → Avalonia 渐变画刷（LinearGradientBrush）。stops<2 或色非法返 null。
    /// 角度同 CSS linear-gradient（0=自下向上，顺时针）。注：radial 类型暂回落 linear（P1 先线性够"梦幻"）。</summary>
    private static IBrush? BuildGradient(GradientSpec spec)
    {
        if (spec.Stops is null || spec.Stops.Count < 2) return null;
        var stops = new List<GradientStop>();
        foreach (var s in spec.Stops)
        {
            if (string.IsNullOrWhiteSpace(s.Color) || !Color.TryParse(s.Color!, out var c)) continue;
            stops.Add(new GradientStop { Color = c, Offset = s.Offset ?? 0 });
        }
        if (stops.Count < 2) return null;
        stops.Sort((a, b) => a.Offset.CompareTo(b.Offset));

        var angle = spec.Angle ?? 135;
        var rad = angle * Math.PI / 180.0;
        // 方向向量 (sinθ, -cosθ)：0°=↑、90°=→、135°=↘。StartPoint/EndPoint 居中 ± 半方向，覆盖元素四角。
        var lg = new LinearGradientBrush
        {
            StartPoint = new RelativePoint(0.5 - 0.5 * Math.Sin(rad), 0.5 + 0.5 * Math.Cos(rad), RelativeUnit.Relative),
            EndPoint = new RelativePoint(0.5 + 0.5 * Math.Sin(rad), 0.5 - 0.5 * Math.Cos(rad), RelativeUnit.Relative),
        };
        foreach (var s in stops) lg.GradientStops.Add(s);
        return lg;
    }

    /// <summary>把 JSON overlay 应用到目标元素的 Resources.MergedDictionaries。
    /// 内置 WorkbenchTheme（经 XAML ResourceInclude 合入）不动；overlay 追加覆盖其键。
    /// 若皮肤 JSON 声明了 <see cref="ThemeDefinition.Styles"/>（.axaml 样式包 URI），则同时用
    /// StyleInclude 载入该 .axaml 并 merge 进 target.Styles——Codex-CSS 式特效/控制模板换肤。
    /// 再次调用时先移除旧 overlay + 旧 styles（按目标跟踪），实现换肤。</summary>
    public static void Apply(StyledElement target, string? customJson)
    {
        // 移除旧 token overlay
        if (_overlays.TryGetValue(target, out var prior))
        {
            target.Resources.MergedDictionaries.Remove(prior);
            _overlays.Remove(target);
        }
        // 移除旧皮肤 .axaml 样式包
        if (_styleOverlays.TryGetValue(target, out var priorStyle))
        {
            target.Styles.Remove(priorStyle);
            _styleOverlays.Remove(target);
        }
        if (string.IsNullOrWhiteSpace(customJson)) return;
        var def = Deserialize(customJson);
        if (def is null) return;
        // token overlay：颜色/渐变/字体/圆角/间距/背景 → Resources.MergedDictionaries
        var overlay = ToResourceDictionary(def);
        target.Resources.MergedDictionaries.Add(overlay);
        _overlays.Add(target, overlay);
        // styles overlay：.axaml 特效/控制模板 → target.Styles（Codex-CSS 式换肤）
        if (!string.IsNullOrWhiteSpace(def.Styles))
        {
            var style = LoadSkinStyles(def.Styles);
            if (style is not null)
            {
                target.Styles.Add(style);
                _styleOverlays.Add(target, style);
            }
        }
    }

    /// <summary>按 URI 载入皮肤 .axaml 样式包（&lt;Styles&gt; 根的编译资源）。avares:// 走 StyleInclude 懒载。
    /// 失败（资源不存在/解析错）返 null——静默跳过，token overlay 仍生效（特效是锦上添花非必需）。</summary>
    private static IStyle? LoadSkinStyles(string stylesUri)
    {
        try
        {
            var uri = new Uri(stylesUri, UriKind.RelativeOrAbsolute);
            // StyleInclude：avares:// 编译资源懒载（baseUri=同 URI，Source=绝对 avares 即可）。
            return new StyleInclude(uri) { Source = uri };
        }
        catch
        {
            return null;
        }
    }
}

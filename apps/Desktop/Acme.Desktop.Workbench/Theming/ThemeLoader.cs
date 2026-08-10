using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Styling;

namespace Acme.Desktop.Workbench.Theming;

/// <summary>
/// JSON 主题加载器：ThemeDefinition（JSON）→ 运行时 ResourceDictionary（Color/SolidColorBrush/FontFamily/CornerRadius/Thickness/ImageBrush）。
/// 叠加在 WorkbenchTheme.axaml（编译期 token 基线）之上。内置 light（light.json）与 dark（dark.json）同为 JSON overlay——
/// 换 JSON 即换肤，无需重编；用户可在 &lt;tenantDir&gt;/themes/*.json 放自定义主题，或同名 light.json/dark.json 覆盖内置。
/// 全部经 {DynamicResource} 消费 → overlay merge 后全量重染（颜色/字体/圆角/间距/背景）。
/// </summary>
public static class ThemeLoader
{
    private static readonly JsonSerializerOptions _jsonOpts = new() { PropertyNameCaseInsensitive = true };

    private static readonly ConditionalWeakTable<StyledElement, ResourceDictionary> _overlays = new();

    /// <summary>反序列化 JSON。失败返回 null。</summary>
    public static ThemeDefinition? Deserialize(string json)
    {
        try { return JsonSerializer.Deserialize<ThemeDefinition>(json, _jsonOpts); }
        catch { return null; }
    }

    /// <summary>读内嵌 dark 主题（Assets/dark.json，Catppuccin Mocha）为字符串。内置 dark = 此 JSON overlay（与 <see cref="GetLightJson"/> 对称）。</summary>
    public static string? GetDarkJson()
    {
        var uri = new Uri("avares://Acme.Desktop.Workbench/Assets/dark.json");
        if (!AssetLoader.Exists(uri)) return null;
        using var s = AssetLoader.Open(uri);
        using var r = new StreamReader(s);
        return r.ReadToEnd();
    }

    /// <summary>读内嵌 light 主题（Assets/light.json）为字符串。内置 light 也是 JSON overlay（与 dark 对称），
    /// 不再是「无 overlay」——值与 SharedTheme.axaml 基线一致，此处显式化使其可被用户 themes/light.json 覆盖。</summary>
    public static string? GetLightJson()
    {
        var uri = new Uri("avares://Acme.Desktop.Workbench/Assets/light.json");
        if (!AssetLoader.Exists(uri)) return null;
        using var s = AssetLoader.Open(uri);
        using var r = new StreamReader(s);
        return r.ReadToEnd();
    }

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

    /// <summary>把 JSON overlay 应用到目标元素的 Resources.MergedDictionaries。
    /// 内置 WorkbenchTheme（经 XAML ResourceInclude 合入）不动；overlay 追加覆盖其键。
    /// 再次调用时先移除旧 overlay（按目标跟踪），实现换肤。</summary>
    public static void Apply(StyledElement target, string? customJson)
    {
        if (_overlays.TryGetValue(target, out var prior))
        {
            target.Resources.MergedDictionaries.Remove(prior);
            _overlays.Remove(target);
        }
        if (string.IsNullOrWhiteSpace(customJson)) return;
        var def = Deserialize(customJson);
        if (def is null) return;
        var overlay = ToResourceDictionary(def);
        target.Resources.MergedDictionaries.Add(overlay);
        _overlays.Add(target, overlay);
    }
}

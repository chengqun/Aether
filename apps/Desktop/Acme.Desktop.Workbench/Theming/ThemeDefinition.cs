using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace Acme.Desktop.Workbench.Theming;

/// <summary>JSON 主题 DTO。用户自定义主题：颜色/字体/圆角/间距/背景。经 <see cref="ThemeLoader"/> 反序列化成运行时 ResourceDictionary。</summary>
public sealed class ThemeDefinition
{
    /// <summary>颜色资源：key → 值。以 "Brush" 结尾 → SolidColorBrush；以 "Color" 结尾 → Color（并自动派生同名 Brush）。
    /// 覆盖 SharedTheme/WorkbenchTheme 的 AccentBrush/SidebarBgBrush/ContentBgBrush/BorderBrush/TextPrimaryBrush 等。</summary>
    [JsonPropertyName("colors")]
    public Dictionary<string, string> Colors { get; set; } = new();

    /// <summary>字体资源：key → FontFamily 串。如 "UiFont"/"MonoFont"/"CodeFont"。</summary>
    [JsonPropertyName("fonts")]
    public Dictionary<string, string> Fonts { get; set; } = new();

    /// <summary>圆角 token：key → 数值串。如 "CornerRadiusMd"。</summary>
    [JsonPropertyName("radius")]
    public Dictionary<string, string> Radius { get; set; } = new();

    /// <summary>间距 token：key → 数值串。如 "SpaceMd"。</summary>
    [JsonPropertyName("spacing")]
    public Dictionary<string, string> Spacing { get; set; } = new();

    /// <summary>背景资源（WorkspaceBackground）：图片 asset 或纯色 fallback。</summary>
    [JsonPropertyName("background")]
    public BackgroundDef? Background { get; set; }
}

/// <summary>背景定义。</summary>
public sealed class BackgroundDef
{
    /// <summary>图片 asset 路径（avares:// 或文件路径）。优先于 <see cref="Color"/>。</summary>
    [JsonPropertyName("image")]
    public string? Image { get; set; }

    /// <summary>纯色 fallback（hex）。</summary>
    [JsonPropertyName("color")]
    public string? Color { get; set; }

    /// <summary>拉伸模式：None/Fill/Uniform/UniformToFill（默认 UniformToFill）。</summary>
    [JsonPropertyName("stretch")]
    public string? Stretch { get; set; }
}

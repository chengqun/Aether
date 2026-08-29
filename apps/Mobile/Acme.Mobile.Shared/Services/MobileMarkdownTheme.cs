using Indiko.Maui.Controls.Markdown.Theming;

namespace Acme.Mobile.Shared.Services;

/// <summary>
/// 构建 <see cref="MarkdownView"/> 主题：light/dark 双 palette **从 <c>Colors.xaml</c> 动态资源读取**
/// （而非硬编码 hex），配合 <c>MarkdownView.UseAppTheme="True"</c> 随 <c>Application.Current.UserAppTheme</c>
/// （由 <c>MauiThemeManager.Apply()</c> 设置）自动切换。这样 markdown 的颜色与页面其它 <c>AppThemeBinding</c>
/// 颜色同源，改主题 token 一处生效；主题切换时宿主调 <see cref="Rebuild"/> 重建资源即可即时刷新。
/// <para>Background=Transparent 让气泡/页面背景透出（与桌面 Empty+token 绑定同思路）。
/// 之前用 <c>MarkdownThemeDefaults.GitHub.Clone()</c> 无 PaletteDark + 无 UseAppTheme →
/// dark 模式下 markdown 仍 light 配色（白底深字），与已切换的气泡背景脱节。</para>
/// </summary>
public static class MobileMarkdownTheme
{
    /// <summary>资源 key（host App 在 <c>Resources["AppMarkdownTheme"]</c> 挂载，XAML <c>{StaticResource AppMarkdownTheme}</c> 引用）。</summary>
    public const string ResourceKey = "AppMarkdownTheme";

    public static MarkdownTheme Build(ResourceDictionary? tokens = null)
    {
        tokens ??= Application.Current?.Resources;
        Color Res(string key, string fallbackHex)
            => tokens is { } t && t.TryGetValue(key, out var v) && v is Color c ? c : Color.FromArgb(fallbackHex);

        // light palette 基于 GitHub 预设（排版/标题色已配好），把前景/链接色覆盖为 app token（保证与页面一致）
        var light = MarkdownThemeDefaults.GitHub.Clone().Palette!;
        light.Background = Colors.Transparent;
        light.HyperlinkColor = Res("Accent", "#1976D2");
        light.TextPrimary = Res("TextPrimary", "#1A1A1A");
        light.TextSecondary = Res("TextSecondary", "#666666");
        light.Surface = Res("CardBackground", "#FFFFFF");
        light.Primary = Res("Primary", "#2E7D32");

        // dark palette：映射到 app dark token（从 Colors.xaml 动态读，改 token 即生效）
        var dark = new MarkdownPalette
        {
            Background = Colors.Transparent,
            Surface = Res("CardBackgroundDark", "#1E1E1E"),
            Primary = Res("PrimaryDark", "#66BB6A"),
            Secondary = Res("TextSecondaryDark", "#9E9E9E"),
            TextPrimary = Res("TextPrimaryDark", "#E8E8E8"),
            TextSecondary = Res("TextSecondaryDark", "#9E9E9E"),
            TextDisabled = Color.FromArgb("#666666"),
            HyperlinkColor = Color.FromArgb("#64B5F6"),
            H1Color = Res("TextPrimaryDark", "#E8E8E8"),
            H2Color = Res("TextPrimaryDark", "#E8E8E8"),
            H3Color = Res("TextPrimaryDark", "#E8E8E8"),
            H4Color = Res("TextPrimaryDark", "#E8E8E8"),
            H5Color = Res("TextPrimaryDark", "#E8E8E8"),
            H6Color = Res("TextSecondaryDark", "#9E9E9E"),
            CodeBlockBackground = Res("BubbleThinkingDark", "#262626"),
            CodeBlockBorder = Res("DividerDark", "#2C2C2C"),
            CodeBlockText = Res("TextPrimaryDark", "#E8E8E8"),
            BlockQuoteBackground = Res("BubbleThinkingDark", "#262626"),
            BlockQuoteBorder = Res("DividerDark", "#2C2C2C"),
            BlockQuoteText = Res("TextSecondaryDark", "#9E9E9E"),
            TableHeaderBackground = Res("NavBackgroundDark", "#1A1A1A"),
            TableHeaderText = Res("TextPrimaryDark", "#E8E8E8"),
            TableRowBackground = Res("CardBackgroundDark", "#1E1E1E"),
            TableRowText = Res("TextPrimaryDark", "#E8E8E8"),
            TableBorder = Res("DividerDark", "#2C2C2C"),
            DividerColor = Res("DividerDark", "#2C2C2C"),
        };

        return new MarkdownTheme
        {
            Palette = light,
            PaletteDark = dark,
        };
    }

    /// <summary>重建并挂载到 <c>Application.Current.Resources</c>（主题切换时调用，即时刷新已渲染的 MarkdownView）。</summary>
    public static void Rebuild()
    {
        if (Application.Current is null) return;
        // Build() 现在读动态资源 Token，若 Color 值变了，需重建引用；MarkdownView 持有 Theme 引用，
        // 重挂载后经 UseAppTheme 会拿新 palette。直接更新值式字典项（避免 new ResourceDictionary 丢失其它资源）。
        Application.Current.Resources[ResourceKey] = Build();
    }
}

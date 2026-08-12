using Indiko.Maui.Controls.Markdown.Theming;

namespace Acme.Mobile.Shared.Services;

/// <summary>
/// 构建 <see cref="MarkdownView"/> 主题：light/dark 双 palette 映射到 app 颜色 token，
/// 配合 <c>MarkdownView.UseAppTheme="True"</c> 随 <c>Application.Current.UserAppTheme</c>
/// （由 <c>MauiThemeManager.Apply()</c> 设置）自动切换。
/// <para>Background=Transparent 让气泡/页面背景透出（与桌面 Empty+token 绑定同思路）。
/// 之前用 <c>MarkdownThemeDefaults.GitHub.Clone()</c> 无 PaletteDark + 无 UseAppTheme →
/// dark 模式下 markdown 仍 light 配色（白底深字），与已切换的气泡背景脱节。</para>
/// </summary>
public static class MobileMarkdownTheme
{
    public static MarkdownTheme Build()
    {
        // light palette 基于 GitHub 预设（排版/标题色已配好），仅覆盖背景为透明 + 链接色对齐 Accent
        var light = MarkdownThemeDefaults.GitHub.Clone().Palette!;
        light.Background = Colors.Transparent;
        light.HyperlinkColor = Color.FromArgb("#1976D2"); // Accent

        // dark palette：映射到 app dark token（TextPrimaryDark/DividerDark/NavBackgroundDark/CardBackgroundDark）
        var dark = new MarkdownPalette
        {
            Background = Colors.Transparent,
            Surface = Color.FromArgb("#1E1E1E"),       // CardBackgroundDark
            Primary = Color.FromArgb("#66BB6A"),        // PrimaryDark
            Secondary = Color.FromArgb("#9E9E9E"),      // TextSecondaryDark
            TextPrimary = Color.FromArgb("#E8E8E8"),    // TextPrimaryDark
            TextSecondary = Color.FromArgb("#9E9E9E"),  // TextSecondaryDark
            TextDisabled = Color.FromArgb("#666666"),
            HyperlinkColor = Color.FromArgb("#64B5F6"),
            H1Color = Color.FromArgb("#E8E8E8"),
            H2Color = Color.FromArgb("#E8E8E8"),
            H3Color = Color.FromArgb("#E8E8E8"),
            H4Color = Color.FromArgb("#E8E8E8"),
            H5Color = Color.FromArgb("#E8E8E8"),
            H6Color = Color.FromArgb("#9E9E9E"),
            CodeBlockBackground = Color.FromArgb("#262626"),
            CodeBlockBorder = Color.FromArgb("#2C2C2C"),  // DividerDark
            CodeBlockText = Color.FromArgb("#E8E8E8"),
            BlockQuoteBackground = Color.FromArgb("#262626"),
            BlockQuoteBorder = Color.FromArgb("#2C2C2C"),
            BlockQuoteText = Color.FromArgb("#9E9E9E"),
            TableHeaderBackground = Color.FromArgb("#1A1A1A"), // NavBackgroundDark
            TableHeaderText = Color.FromArgb("#E8E8E8"),
            TableRowBackground = Color.FromArgb("#1E1E1E"),   // CardBackgroundDark
            TableRowText = Color.FromArgb("#E8E8E8"),
            TableBorder = Color.FromArgb("#2C2C2C"),
            DividerColor = Color.FromArgb("#2C2C2C"),
        };

        return new MarkdownTheme
        {
            Palette = light,
            PaletteDark = dark,
        };
    }
}

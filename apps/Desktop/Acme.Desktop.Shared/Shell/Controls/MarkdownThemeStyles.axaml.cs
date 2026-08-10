using Avalonia.Markup.Xaml;
using Avalonia.Styling;

namespace Acme.Desktop.Shared.Shell.Controls;

/// <summary>
/// 自有 Markdown 主题样式集（取代 Markdown.Avalonia 内置 GithubLike）。
/// <para>
/// GithubLike 由 MarkdownScrollViewer 构造时 <c>Styles.Insert(0, …)</c> 挂进【控件自身】Styles 集合——
/// 控件级优先级高于 app 级，且其颜色全硬编码浅色（表格 #DFE2E5/#FFFFFF/White/#F6F8FA、代码块 DarkBlue、
/// 链接 Blue/Red…），非 light 主题下表格白块、代码块低对比。与其用 app 级样式去盖（盖不动），不如直接不用它。
/// </para>
/// <para>
/// <see cref="SelectableMarkdownViewer"/> 构造时设 <c>MarkdownStyleName="Empty"</c>（空主题，无任何样式），
/// 再 <c>Styles.Add(new MarkdownThemeStyles())</c> 挂本样式集提供【完整】markdown 样式：布局沿用 GithubLike
/// 的 margin/font-size/padding（视觉不变），颜色全绑 <c>{DynamicResource}</c> token → 跟随窗口主题 overlay。
/// AgentMessageTheme 全元素覆盖：Text / Heading(H1–H6) / Code(block+inline) / Table / Quote / Link / List / Rule。
/// </para>
/// </summary>
public class MarkdownThemeStyles : Styles
{
    public MarkdownThemeStyles()
    {
        AvaloniaXamlLoader.Load(this);
    }
}

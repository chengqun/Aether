using Avalonia.Markup.Xaml;
using Avalonia.Styling;

namespace Acme.Desktop.Shared.Shell.Controls;

/// <summary>
/// Markdown.Avalonia 主题覆盖样式集（控件级）。
/// <para>
/// 根因：MarkdownScrollViewer 构造时 <c>Styles.Insert(0, GithubLike)</c> 把 GithubLike 主题挂进
/// 【控件自身】Styles 集合——控件级样式优先级高于 app 级，故 SharedTheme（app 级）里的 markdown 覆盖
/// （Border.CodeBlock/Border.Table 等）一律不生效。GithubLike 对表格硬编码浅色 → 非 light 主题表格白块。
/// </para>
/// <para>
/// 修：<see cref="SelectableMarkdownViewer"/> 构造时 <c>Styles.Add(new MarkdownThemeStyles())</c> 把本样式集
/// 挂到同一控件 Styles 集合（在 GithubLike 之后）；selector 带 <c>.Markdown_Avalonia_MarkdownViewer</c>
/// 前缀匹配 GithubLike 特异性，同特异性后置者胜 → 覆盖生效。色绑 <c>{DynamicResource}</c> token 跟随主题。
/// </para>
/// </summary>
public class MarkdownThemeStyles : Styles
{
    public MarkdownThemeStyles()
    {
        AvaloniaXamlLoader.Load(this);
    }
}

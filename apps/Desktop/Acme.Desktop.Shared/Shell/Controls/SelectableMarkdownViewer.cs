using System.Reflection;
using System.Text.RegularExpressions;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Markdown.Avalonia;

namespace Acme.Desktop.Shared.Shell.Controls;

/// <summary>
/// 可选中复制的 Markdown 查看器（基于 Markdown.Avalonia 的 MarkdownScrollViewer）。
/// 内置 MarkdownScrollViewer.OnKeyDown 仅处理 Ctrl+C（不认 macOS Cmd+C），且原样返回渲染文本。
/// 本子类重写 OnKeyDown：同时支持 Ctrl+C 与 Cmd+C，并清洗选中文本（去表格填充/多余空白）。
/// 移植自 AlphaAgent 的 SelectableMarkdownViewer。
/// </summary>
public class SelectableMarkdownViewer : MarkdownScrollViewer
{
    public SelectableMarkdownViewer()
    {
        // 控件级主题覆盖：base ctor 已 Styles.Insert(0, GithubLike)——GithubLike 是控件级样式，
        // 优先级高于 app 级 SharedTheme，故 app 级 markdown 覆盖无效。此处把 token 覆盖挂到同一
        // 控件 Styles（在 GithubLike 之后），同特异性后置者胜 → 表格/代码块/引用/链接跟随主题。
        // XAML 后续设 MarkdownStyleName="GithubLike" 时 setter 只 Remove/Insert(0) 旧 _markdownStyle，
        // 不动本覆盖（始终在 GithubLike 之后）。见 MarkdownThemeStyles。
        Styles.Add(new MarkdownThemeStyles());
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        // 点击即获焦，使后续 Ctrl/Cmd+C 能路由到本控件
        Focus();
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.C && (e.KeyModifiers & (KeyModifiers.Control | KeyModifiers.Meta)) != 0)
        {
            var text = Normalize(GetSelectedText());
            if (!string.IsNullOrEmpty(text))
            {
                e.Handled = true; // 抑制内置 OnKeyDown 的原始复制
                var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
                if (clipboard != null)
                    _ = ClipboardExtensions.SetTextAsync(clipboard, text);
                return;
            }
        }
        base.OnKeyDown(e);
    }

    /// <summary>反射 MarkdownScrollViewer._document 调用 DocumentElement.GetSelectedText()</summary>
    private string? GetSelectedText()
    {
        var docField = typeof(MarkdownScrollViewer).GetField("_document",
            BindingFlags.NonPublic | BindingFlags.Instance);
        var doc = docField?.GetValue(this);
        return doc?.GetType().GetMethod("GetSelectedText", Type.EmptyTypes)?.Invoke(doc, null) as string;
    }

    /// <summary>清洗选中文本：统一换行、合并行内多余空格/制表符（表格填充）、压缩连续空行、去行尾空白。</summary>
    private static string Normalize(string? text)
    {
        if (string.IsNullOrEmpty(text)) return string.Empty;
        var normalized = text.Replace("\r\n", "\n").Replace("\r", "\n");
        var lines = normalized.Split('\n');
        for (int i = 0; i < lines.Length; i++)
            lines[i] = Regex.Replace(lines[i], @"[ \t]+", " ").Trim();
        var result = string.Join('\n', lines);
        result = Regex.Replace(result, @"\n{3,}", "\n\n");
        return result.Trim();
    }
}

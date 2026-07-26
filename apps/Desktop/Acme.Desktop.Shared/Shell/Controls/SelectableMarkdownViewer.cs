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

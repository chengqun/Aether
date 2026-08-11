using System.Reflection;
using System.Text.RegularExpressions;
using Acme.Desktop.Mvvm;
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
    // #2 缓存反射：_document 字段与 GetSelectedText 方法在类型上固定不变，每次复制都 GetField/GetMethod
    // 反射查找是纯浪费。缓存为 static，首次解析后零开销。
    private static readonly FieldInfo? _documentField = typeof(MarkdownScrollViewer)
        .GetField("_document", BindingFlags.NonPublic | BindingFlags.Instance);
    private static MethodInfo? _getSelectedTextMethod;

    // #3 缓存正则：Normalize 每次复制都 new 两个 Regex（默认未编译），改 static readonly Compiled。
    private static readonly Regex _inlineSpace = new(@"[ \t]+", RegexOptions.Compiled);
    private static readonly Regex _blankLines = new(@"\n{3,}", RegexOptions.Compiled);

    public SelectableMarkdownViewer()
    {
        // 不用 GithubLike（其硬编码浅色 + 控件级优先级压过 app 级主题）。改用 Empty 空主题，
        // 由 MarkdownThemeStyles 提供完整 markdown 样式（布局 + token 色，跟随窗口主题）。
        // base ctor 已按 ThemeDetector 插了默认主题（FluentTheme/Standard）到 Styles[0]；
        // 此处 MarkdownStyleName="Empty" 触发 setter 移除默认 + Insert(0, 空 Styles)。
        MarkdownStyleName = "Empty";
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
                    ClipboardExtensions.SetTextAsync(clipboard, text).SafeFireAndForget();
                return;
            }
        }
        base.OnKeyDown(e);
    }

    /// <summary>反射 MarkdownScrollViewer._document 调用 DocumentElement.GetSelectedText()</summary>
    private string? GetSelectedText()
    {
        if (_documentField is null) return null;
        var doc = _documentField.GetValue(this);
        if (doc is null) return null;
        // GetSelectedText 定义在 _document 的固定类型上，首次拿到 doc 后缓存 MethodInfo，后续零反射。
        _getSelectedTextMethod ??= doc.GetType().GetMethod("GetSelectedText", Type.EmptyTypes);
        return _getSelectedTextMethod?.Invoke(doc, null) as string;
    }

    /// <summary>清洗选中文本：统一换行、合并行内多余空格/制表符（表格填充）、压缩连续空行、去行尾空白。</summary>
    private static string Normalize(string? text)
    {
        if (string.IsNullOrEmpty(text)) return string.Empty;
        var normalized = text.Replace("\r\n", "\n").Replace("\r", "\n");
        var lines = normalized.Split('\n');
        for (int i = 0; i < lines.Length; i++)
            lines[i] = _inlineSpace.Replace(lines[i], " ").Trim();
        var result = string.Join('\n', lines);
        result = _blankLines.Replace(result, "\n\n");
        return result.Trim();
    }
}

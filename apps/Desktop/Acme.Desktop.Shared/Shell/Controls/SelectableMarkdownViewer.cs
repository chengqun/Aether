using System.Diagnostics;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Windows.Input;
using Acme.Desktop.Mvvm;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.LogicalTree;
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
        // 拦截 markdown 链接点击：note:/note-id: 内部链接转发给 NoteNavigateCommand（由 VM 解析+导航到目标笔记节点）；
        // 其余（http/https/mailto…）回退系统浏览器（复刻库默认 DefaultHyperlinkCommand 行为）。
        // MarkdownScrollViewer 不直接暴露 HyperlinkCommand，但其 Engine（IMarkdownEngine）有公开 HyperlinkCommand——链接点击的总出口。
        SetupHyperlinkCommand();
    }

    protected override void OnAttachedToLogicalTree(LogicalTreeAttachmentEventArgs e)
    {
        base.OnAttachedToLogicalTree(e);
        // Engine 可能在控件附加到逻辑树时才完成初始化（构造期可能为 null），此处兜底再设一次。
        SetupHyperlinkCommand();
    }

    private void SetupHyperlinkCommand()
    {
        // Engine 声明为 IMarkdownEngineBase，但默认引擎实现 IMarkdownEngine（带 HyperlinkCommand 链接出口）。
        if (Engine is IMarkdownEngine md)
            md.HyperlinkCommand = new NoteLinkHyperlinkCommand(this);
    }

    /// <summary>
    /// 内部笔记链接导航命令。markdown 里的 <c>[文字](note:标题)</c> / <c>[文字](note-id:&lt;guid&gt;)</c>
    /// 链接点击时，本控件把原始 url（含 scheme 前缀）作为参数传给此命令，由绑定方（NotesViewModel）解析并跳转。
    /// 不绑定则内部链接点击无反应。控件本身不依赖任何 VM 类型——只做 scheme 分流 + 转发 + 浏览器回退。
    /// </summary>
    public static readonly StyledProperty<ICommand?> NoteNavigateCommandProperty =
        AvaloniaProperty.Register<SelectableMarkdownViewer, ICommand?>(nameof(NoteNavigateCommand));

    public ICommand? NoteNavigateCommand
    {
        get => GetValue(NoteNavigateCommandProperty);
        set => SetValue(NoteNavigateCommandProperty, value);
    }

    /// <summary>
    /// markdown 链接点击分流命令：内部链接（note:/note-id:）转发给 <see cref="NoteNavigateCommand"/>，
    /// 外部链接回退系统浏览器。挂在 <see cref="MarkdownScrollViewer.HyperlinkCommand"/> 上。
    /// </summary>
    private sealed class NoteLinkHyperlinkCommand : ICommand
    {
        private readonly SelectableMarkdownViewer _owner;
        public NoteLinkHyperlinkCommand(SelectableMarkdownViewer owner) => _owner = owner;
        public event EventHandler? CanExecuteChanged { add { } remove { } }
        public bool CanExecute(object? parameter) => true;

        public void Execute(object? parameter)
        {
            if (parameter is not string url || string.IsNullOrWhiteSpace(url)) return;
            var trimmed = url.Trim();
            if (trimmed.StartsWith("note-id:", StringComparison.OrdinalIgnoreCase)
                || trimmed.StartsWith("note:", StringComparison.OrdinalIgnoreCase))
            {
                // 内部链接：交给绑定的导航命令（参数=原始 url，含前缀，由 VM 解析）
                _owner.NoteNavigateCommand?.Execute(trimmed);
                return;
            }
            // 外部链接：回退系统浏览器（复刻库默认 DefaultHyperlinkCommand）
            try
            {
                if (System.Uri.TryCreate(trimmed, System.UriKind.Absolute, out _))
                    Process.Start(new ProcessStartInfo(trimmed) { UseShellExecute = true });
            }
            catch { /* 无效 url / 无默认浏览器：忽略 */ }
        }
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

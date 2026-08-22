using System;
using System.ComponentModel;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using Acme.Desktop.Mvvm;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using AvaloniaEdit;

namespace Acme.Desktop.Shared.Shell;

/// <summary>浏览器网络面板视图（DevTools/BurpSuite 式 HTTP 请求记录）。
/// 布局：工具栏/会话标签/过滤栏/请求列表(上) + GridSplitter(可拖) + 详情(下，Tab：概览/请求/响应)。
/// 详情 body 用 AvaloniaEdit 只读渲染 + <see cref="CodeEditorSetup"/> 语法高亮（按 mime 分流 json/html/xml...）；
/// 请求/响应头美化 JSON。SelectedRequest 变（VM PropertyChanged）→ 刷新 4 个 TextEditor；切详情 tab 也重读（兼容 body 异步回填）。</summary>
public partial class BrowserView : UserControl
{
    private BrowserViewModel? _vm;

    public BrowserView()
    {
        InitializeComponent();
        Loaded += OnLoaded;
    }

    private void OnLoaded(object? sender, RoutedEventArgs e)
    {
        // 编辑器外观（当前行高亮 + Ctrl+F 搜索面板）——4 个详情编辑器各配一次。
        ConfigureEditor(RequestHeadersEditor);
        ConfigureEditor(RequestBodyEditor);
        ConfigureEditor(ResponseHeadersEditor);
        ConfigureEditor(ResponseBodyEditor);
        RefreshDetailEditors();
    }

    private static void ConfigureEditor(TextEditor editor)
    {
        try { CodeEditorSetup.ConfigureAppearance(editor); }
        catch { /* 控件未就绪则跳过 */ }
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (_vm is not null) _vm.PropertyChanged -= OnVmPropertyChanged;
        _vm = DataContext as BrowserViewModel;
        if (_vm is not null) _vm.PropertyChanged += OnVmPropertyChanged;
        RefreshDetailEditors();
    }

    private void OnVmPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(BrowserViewModel.SelectedRequest))
            RefreshDetailEditors();
    }

    /// <summary>按当前 SelectedRequest 刷新 4 个详情 TextEditor（请求/响应头美化 JSON、请求/响应体按 mime 高亮、二进制显占位）。</summary>
    private void RefreshDetailEditors()
    {
        var req = _vm?.SelectedRequest;
        if (req is null)
        {
            SyncEditor(RequestHeadersEditor, "");
            SyncEditor(RequestBodyEditor, "");
            SyncEditor(ResponseHeadersEditor, "");
            SyncEditor(ResponseBodyEditor, "");
            return;
        }
        SyncEditor(RequestHeadersEditor, PrettifyJson(req.RequestHeaders));
        SyncEditor(RequestBodyEditor, req.RequestBody);
        SyncEditor(ResponseHeadersEditor, PrettifyJson(req.ResponseHeaders));
        var bodyText = req.IsBinary
            ? $"[二进制响应体 {req.Size ?? 0} 字节，不存文本——用 browser_get_cookies + curl 重发获取]"
            : req.ResponseBody;
        SyncEditor(ResponseBodyEditor, bodyText);
        // 语法高亮：头是 JSON、响应体按 mime、请求体若像 JSON 则 json 高亮。
        CodeEditorSetup.ApplyHighlighting(RequestHeadersEditor, "json");
        CodeEditorSetup.ApplyHighlighting(ResponseHeadersEditor, "json");
        CodeEditorSetup.ApplyHighlighting(RequestBodyEditor, LooksJson(req.RequestBody) ? "json" : null);
        CodeEditorSetup.ApplyHighlighting(ResponseBodyEditor, req.IsBinary ? null : SyntaxForMime(req.MimeType));
    }

    /// <summary>设 TextEditor.Text——TextArea 未就绪则 Post 重试（照 NotesEditorView SyncCodeEditor 范式）。</summary>
    private static void SyncEditor(TextEditor editor, string? text)
    {
        var t = text ?? string.Empty;
        if (editor.TextArea is null) { Dispatcher.UIThread.Post(() => SyncEditor(editor, t)); return; }
        if (editor.Text != t) editor.Text = t;
    }

    /// <summary>美化 JSON 串（缩进）；非 JSON 或解析失败原样返回。</summary>
    private static string? PrettifyJson(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return s;
        try { return JsonNode.Parse(s)?.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) ?? s; }
        catch { return s; }
    }

    private static bool LooksJson(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return false;
        var c = s.TrimStart();
        return c.Length > 0 && (c[0] == '{' || c[0] == '[');
    }

    /// <summary>mime → 语法 key（CodeEditorSetup 接受的名字：json/html/xml/css/javascript）。</summary>
    private static string? SyntaxForMime(string? mime)
    {
        if (string.IsNullOrEmpty(mime)) return null;
        if (mime.Contains("json")) return "json";
        if (mime.Contains("html")) return "html";
        if (mime.Contains("xml")) return "xml";
        if (mime.Contains("css")) return "css";
        if (mime.Contains("javascript") || mime.Contains("ecmascript")) return "javascript";
        return null;
    }

    // —— 地址栏 / 会话标签（保留） ——

    private void OnAddressKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && DataContext is BrowserViewModel vm)
        {
            vm.NavigateCommand.Execute(null);
            e.Handled = true;
        }
    }

    private void OnTabPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is Control c && c.DataContext is BrowserSessionItem item && DataContext is BrowserViewModel vm)
            vm.SwitchSessionCommand.Execute(item);
    }

    private void OnCloseTab(object? sender, RoutedEventArgs e)
    {
        if (sender is Button b && b.DataContext is BrowserSessionItem item && DataContext is BrowserViewModel vm)
            vm.CloseSessionCommand.Execute(item);
    }

    // —— 状态段按钮组（fseg） ——

    private void OnStatusSeg(object? sender, RoutedEventArgs e)
    {
        if (sender is Button b && b.Tag is string tag && DataContext is BrowserViewModel vm)
        {
            vm.SetStatusFilterCommand.Execute(tag);
            // 更新 active 样式：遍历同级 Button，仅当前高亮（照 ActivityView.range 范式）。
            if (b.Parent is Panel panel)
                foreach (var c in panel.Children.OfType<Button>())
                    c.Classes.Set("active", ReferenceEquals(c, b));
        }
    }

    // —— 详情 Tab（dtab）切换：叠放 pane 切 IsVisible（不销毁，保留滚动/渲染状态） ——

    private void OnDetailTab(object? sender, RoutedEventArgs e)
    {
        if (sender is Button b && b.Tag is string tag)
        {
            SummaryPane.IsVisible = tag == "summary";
            RequestPane.IsVisible = tag == "request";
            ResponsePane.IsVisible = tag == "response";
            if (b.Parent is Panel panel)
                foreach (var c in panel.Children.OfType<Button>())
                    c.Classes.Set("active", ReferenceEquals(c, b));
            // 切 tab 时重读详情（兼容 body 异步回填后切到响应 tab 才刷新）。
            RefreshDetailEditors();
        }
    }
}

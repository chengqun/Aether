using System;
using System.ComponentModel;
using Acme.Desktop.Mvvm;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using AvaloniaEdit.Folding;

namespace Acme.Desktop.Shared.Shell;

/// <summary>
/// 文档页编辑器视图（从原 <c>DocumentsView</c> col2 拆出）。标签条 + 状态消息 + markdown 上下分栏 + AvaloniaEdit 单实例编辑器 + 空态。
/// 编辑器单实例随活动 tab 换内容（镜像 NotesView 单编辑器随选中节点换内容）。
/// 与 <see cref="FileTreeView"/> 共享同一 <see cref="DocumentsViewModel"/> 单例——树点文件→VM.Open→ActiveTab 变→本视图 SyncEditor 响应。
/// 编辑器配置/高亮/折叠/双配色委托 <see cref="CodeEditorSetup"/>（与 NotesView 共用）。
/// </summary>
public partial class DocumentsEditorView : UserControl
{
    private DocumentsViewModel? _vm;
    private bool _syncing;
    private FoldingManager? _foldingManager;

    public DocumentsEditorView()
    {
        InitializeComponent();
        CodeEditor.TextChanged += OnCodeEditorTextChanged;
        CodeEditor.PointerWheelChanged += OnCodeEditorWheelChanged;
        CodeEditor.AddHandler(InputElement.KeyDownEvent, OnEditorKeyDown,
            Avalonia.Interactivity.RoutingStrategies.Tunnel, handledEventsToo: true);
        CodeEditor.Loaded += (_, _) =>
        {
            _foldingManager = FoldingManager.Install(CodeEditor.TextArea);
            CodeEditorSetup.ConfigureAppearance(CodeEditor);
            SyncEditor();
        };
        // 主题明暗切换时重染编辑器（语法着色/当前行/选中色随 ActualThemeVariant 重设）。
        CodeEditor.ActualThemeVariantChanged += (_, _) =>
        {
            CodeEditorSetup.ApplyTheme(CodeEditor);
            if (_vm?.ActiveTab is { } tab)
                CodeEditorSetup.ApplyHighlighting(CodeEditor, tab.SyntaxKey);
        };
        this.Loaded += (_, _) => SyncEditor();
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (_vm != null)
        {
            _vm.PropertyChanged -= OnVmPropertyChanged;
            _vm.TabExternallyReloaded -= OnTabExternallyReloaded;
        }
        _vm = DataContext as DocumentsViewModel;
        if (_vm != null)
        {
            _vm.PropertyChanged += OnVmPropertyChanged;
            _vm.TabExternallyReloaded += OnTabExternallyReloaded;
            SyncEditor();
        }
    }

    /// <summary>外部（agent/外部编辑器）写盘后标签静默重载：若重载的是当前活动标签且非用户正在输入，把编辑器刷新到新内容。
    /// 用户键入走 OnCodeEditorTextChanged→tab.Content=，不发此事件，故不打架。</summary>
    private void OnTabExternallyReloaded(DocumentTab tab)
    {
        if (_vm is null || !_syncing && ReferenceEquals(_vm.ActiveTab, tab))
            SyncEditor();
    }

    private void OnVmPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(DocumentsViewModel.ActiveTab))
            SyncEditor();
        // markdown 上下分栏布局全由 VM 派生属性绑定驱动（IsPreviewPaneVisible/IsSplitterVisible/
        // IsEditorPaneVisible/EditorRow/EditorRowSpan/PreviewRowSpan），code-behind 无需介入。
    }

    /// <summary>把活动 tab 内容载入编辑器并按语法设高亮 + 折叠。</summary>
    private void SyncEditor()
    {
        var tab = _vm?.ActiveTab;
        // 空态切换
        EmptyState.IsVisible = tab is null;
        CodeEditor.IsVisible = tab is not null;
        if (tab is null) return;
        // Document 在控件模板应用（首次显示）后才创建；未就绪时延迟到下一帧再试。
        if (CodeEditor.Document is null)
        {
            Dispatcher.UIThread.Post(SyncEditor);
            return;
        }
        _syncing = true;
        try { CodeEditor.Text = tab.Content ?? ""; }
        finally { _syncing = false; }
        CodeEditorSetup.ApplyHighlighting(CodeEditor, tab.SyntaxKey);
        CodeEditorSetup.UpdateFoldings(_foldingManager, CodeEditor.Document.Text, tab.SyntaxKey);
    }

    private void OnCodeEditorTextChanged(object? sender, EventArgs e)
    {
        if (_syncing) return;
        if (_vm?.ActiveTab is { } tab)
            tab.Content = CodeEditor.Text ?? ""; // 置 IsDirty
        CodeEditorSetup.UpdateFoldings(_foldingManager, CodeEditor.Document.Text, _vm?.ActiveTab?.SyntaxKey);
    }

    /// <summary>Ctrl+滚轮缩放编辑器字体（VSCode 风，8~48，持久化）。</summary>
    private void OnCodeEditorWheelChanged(object? sender, PointerWheelEventArgs e)
    {
        if ((e.KeyModifiers & KeyModifiers.Control) == 0 || _vm is null) return;
        var delta = e.Delta.Y;
        if (delta == 0) return;
        var size = Math.Max(8, Math.Min(48, _vm.EditorFontSize + (delta > 0 ? 1 : -1)));
        if (Math.Abs(size - _vm.EditorFontSize) < 0.01) return;
        _vm.EditorFontSize = size; // 绑定更新 CodeEditor.FontSize，并持久化
        e.Handled = true;
    }

    private void OnEditorKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.S && (e.KeyModifiers & (KeyModifiers.Control | KeyModifiers.Meta)) != 0 && _vm is not null)
        {
            e.Handled = true;
            if (_vm.SaveActiveCommand.CanExecute(null))
                _vm.SaveActiveCommand.Execute(null);
        }
    }

    private void OnCloseClick(object? sender, RoutedEventArgs e)
    {
        if (_vm?.ActiveTab is { } tab) _vm.CloseTab(tab);
    }

    private void OnCloseTabClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Button b && b.DataContext is DocumentTab tab && _vm is not null)
            _vm.CloseTab(tab);
    }

    /// <summary>标签上「重载」按钮：丢弃本地改动，用磁盘内容覆盖（处理 IsStale 冲突）。</summary>
    private void OnReloadStaleTabClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Button b && b.DataContext is DocumentTab tab && _vm is not null)
            _vm.ReloadStaleTabCommand.Execute(tab);
    }
}

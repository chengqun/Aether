using System;
using System.ComponentModel;
using System.Threading.Tasks;
using Acme.Desktop.Mvvm;
using Acme.Desktop.Mvvm.Syntax;
using Microsoft.Extensions.DependencyInjection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using AvaloniaEdit.Folding;

namespace Acme.Desktop.Shared.Shell;

/// <summary>
/// 「知识对象」页视图（文档版 <c>.ok</c> 知识对象 nav 页）。左 <c>.ok</c> 树 + 右上下两栏
///（上=<see cref="DocumentKnowledgeView"/> 表单，下=<see cref="AvaloniaEdit.TextEditor"/> YAML/markdown 源码）。
/// 表单↔源码双向同步照 <see cref="DocumentsEditorView"/> 的 <c>.ok</c> 同步范式：
/// View 持 <c>_syncing</c> 守卫——表单→源码（<see cref="KnowledgeObjectsViewModel.KnowledgeSourceUpdated"/>）设
/// <c>CodeEditor.Text</c> 时早返 <c>OnCodeEditorTextChanged</c>；源码→表单（<c>OnCodeEditorTextChanged</c>）调
/// <see cref="KnowledgeObjectsViewModel.SyncKnowledgeFromSource"/>，VM 内 <c>_syncing</c> 不回环。
/// <c>.ok</c> 语法=markdown（经 <see cref="SyntaxMap"/>），编辑器配置/高亮/折叠/双配色委托 <see cref="CodeEditorSetup"/>。
/// </summary>
public partial class KnowledgeObjectsView : UserControl
{
    private KnowledgeObjectsViewModel? _vm;
    private bool _syncing;
    private bool _layoutPersistWired;
    private FoldingManager? _foldingManager;

    /// <summary>.ok 文件语法 key（经 SyntaxMap → markdown，供 AvaloniaEdit 高亮/折叠）。</summary>
    private static readonly string OkSyntax = SyntaxMap.SyntaxForExtension(".ok");

    public KnowledgeObjectsView()
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
        // 主题明暗切换时重染编辑器（.ok=markdown 高亮随 ActualThemeVariant 重设）。
        CodeEditor.ActualThemeVariantChanged += (_, _) =>
        {
            CodeEditorSetup.ApplyTheme(CodeEditor);
            CodeEditorSetup.ApplyHighlighting(CodeEditor, OkSyntax);
        };
        this.Loaded += (_, _) =>
        {
            SyncEditor();
            RestoreLayout();
            WireHostWindowClosing();
        };
    }

    /// <summary>订阅宿主窗口 Closing，关闭时自持久化上下分栏比例。双订阅守卫防 Loaded 多次触发重复挂事件。</summary>
    private void WireHostWindowClosing()
    {
        if (_layoutPersistWired) return;
        if (TopLevel.GetTopLevel(this) is Window host)
        {
            host.Closing += (_, _) => PersistLayout();
            _layoutPersistWired = true;
        }
    }

    /// <summary>持久化上下分栏比例（ko.previewStar/editorStar）。后台线程写文件，Wait 确保关闭前落盘。</summary>
    private void PersistLayout()
    {
        var store = DesktopApp.Services.GetService<IPreferencesStore>();
        if (store is null) return;
        var (previewStar, editorStar) = CollectLayout();
        try
        {
            Task.Run(async () =>
            {
                await store.SetAsync("ko.previewStar", previewStar.ToString("0.###"));
                await store.SetAsync("ko.editorStar", editorStar.ToString("0.###"));
            }).Wait();
        }
        catch { /* 关闭期写入失败忽略 */ }
    }

    /// <summary>恢复上下分栏比例。JsonPreferencesStore.GetAsync 同步完成，Loaded 期直接读。</summary>
    private void RestoreLayout()
    {
        var store = DesktopApp.Services.GetService<IPreferencesStore>();
        if (store is null) return;
        try
        {
            var ps = double.TryParse(store.GetAsync("ko.previewStar").GetAwaiter().GetResult(), out var p) ? p : 1d;
            var es = double.TryParse(store.GetAsync("ko.editorStar").GetAwaiter().GetResult(), out var e) ? e : 1d;
            if (ps <= 0) ps = 1d;
            if (es <= 0) es = 1d;
            EditorGrid.RowDefinitions[0].Height = new GridLength(ps, GridUnitType.Star);
            EditorGrid.RowDefinitions[2].Height = new GridLength(es, GridUnitType.Star);
        }
        catch { /* 读取失败用默认分隔 */ }
    }

    /// <summary>采集当前上下分栏比例（供关闭时持久化，隐藏页也不丢）。</summary>
    public (double PreviewStar, double EditorStar) CollectLayout()
        => (EditorGrid.RowDefinitions[0].Height.Value,
            EditorGrid.RowDefinitions[2].Height.Value);

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (_vm != null)
        {
            _vm.PropertyChanged -= OnVmPropertyChanged;
            _vm.KnowledgeSourceUpdated -= OnKnowledgeSourceUpdated;
            _vm.KnowledgeExternallyReloaded -= OnKnowledgeExternallyReloaded;
        }
        _vm = DataContext as KnowledgeObjectsViewModel;
        if (_vm != null)
        {
            _vm.PropertyChanged += OnVmPropertyChanged;
            _vm.KnowledgeSourceUpdated += OnKnowledgeSourceUpdated;
            _vm.KnowledgeExternallyReloaded += OnKnowledgeExternallyReloaded;
            SyncEditor();
        }
    }

    /// <summary>表单→源码：表单字段变→VM Serialize 生成新源码→更新 CurrentSource 后发此事件。
    /// _syncing 守卫使 OnCodeEditorTextChanged 早返（不回环 SyncFromSource）。</summary>
    private void OnKnowledgeSourceUpdated(string newSource)
    {
        if (_syncing) return;
        _syncing = true;
        try { CodeEditor.Text = newSource; }
        finally { _syncing = false; }
        CodeEditorSetup.ApplyHighlighting(CodeEditor, OkSyntax);
        CodeEditorSetup.UpdateFoldings(_foldingManager, CodeEditor.Document.Text, OkSyntax);
    }

    /// <summary>外部（agent/外部编辑器）写盘后当前 .ok 透明重载：刷新编辑器 + 重解析表单。
    /// SyncEditor 走 _syncing 不触发表单同步路径，须显式补 SyncFromSource。</summary>
    private void OnKnowledgeExternallyReloaded()
    {
        if (_syncing) return;
        SyncEditor();
        _vm?.SyncKnowledgeFromSource(_vm.CurrentSource);
    }

    private void OnVmPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(KnowledgeObjectsViewModel.ActiveKnowledgeVm))
            SyncEditor();
    }

    /// <summary>把活动 .ok 源码载入编辑器并按 markdown 语法设高亮 + 折叠。</summary>
    private void SyncEditor()
    {
        var vm = _vm;
        var hasActive = vm?.HasActiveKnowledge ?? false;
        if (CodeEditor is not null)
            CodeEditor.IsVisible = hasActive;
        if (!hasActive) return;
        // Document 在控件模板应用（首次显示）后才创建；未就绪时延迟到下一帧再试。
        if (CodeEditor.Document is null)
        {
            Dispatcher.UIThread.Post(SyncEditor);
            return;
        }
        _syncing = true;
        try { CodeEditor.Text = vm?.CurrentSource ?? ""; }
        finally { _syncing = false; }
        CodeEditorSetup.ApplyHighlighting(CodeEditor, OkSyntax);
        CodeEditorSetup.UpdateFoldings(_foldingManager, CodeEditor.Document.Text, OkSyntax);
    }

    private void OnCodeEditorTextChanged(object? sender, EventArgs e)
    {
        if (_syncing || _vm is null) return;
        // 源码→表单：用户在下栏编辑源码→更新 VM CurrentSource + 置 dirty + 重解析回填表单（VM _syncing 守卫不回环）
        _vm.SyncKnowledgeFromSource(CodeEditor.Text ?? "");
        CodeEditorSetup.UpdateFoldings(_foldingManager, CodeEditor.Document.Text, OkSyntax);
    }

    /// <summary>Ctrl+滚轮缩放编辑器字体（VSCode 风，8~48，持久化 ko.editorFontSize）。</summary>
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
            if (_vm.SaveCommand.CanExecute(null))
                _vm.SaveCommand.Execute(null);
        }
    }

}

using System;
using System.ComponentModel;
using System.Linq;
using System.IO;
using System.Threading.Tasks;
using System.Xml;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using AvaloniaEdit.Folding;
using AvaloniaEdit.Highlighting;
using AvaloniaEdit.Highlighting.Xshd;
using AvaloniaEdit.Search;
using Acme.Notes.Domain;
using Acme.Notes.Domain.Knowledge;
using Acme.Desktop.Mvvm;
using Microsoft.Extensions.DependencyInjection;

namespace Acme.Desktop.Shared.Shell;

/// <summary>笔记编辑器（从原 NotesView col2 拆出）：标题栏 + 上下分栏（上=预览/form 表单，下=源码 AvaloniaEdit）+ 6 种 form 节点视图。
/// 与 NotesTreeView 共享同一 NotesViewModel 单例——VM.SelectedNode 变 → OnVmPropertyChanged 建表单 + SyncCodeEditor。
/// 上下分栏比例自持久化（notes.previewStar/editorStar）；树宽已上移 shell split（workbench.layout 持久化）。</summary>
public partial class NotesEditorView : UserControl
{
    private NotesViewModel? _vm;
    private NoteNodeVM? _subscribedNode;
    private bool _syncing;
    private FoldingManager? _foldingManager;

    public NotesEditorView()
    {
        InitializeComponent();
        CodeEditor.TextChanged += OnCodeEditorTextChanged;
        CodeEditor.PointerWheelChanged += OnCodeEditorWheelChanged;
        // 隧道 KeyDown：在 AvaloniaEdit 默认粘贴之前接管 markdown 节点的 Ctrl/Cmd+V（图片→本地存档+markdown 引用）
        CodeEditor.AddHandler(InputElement.KeyDownEvent, OnCodeEditorPreviewKeyDown, RoutingStrategies.Tunnel, handledEventsToo: true);
        CodeEditor.Loaded += (_, _) =>
        {
            ConfigureEditorAppearance();
            SyncCodeEditor();
        };
        // 主题明暗切换 + 主题 JSON 换肤（含 variant 相同但 editor 段不同）时重染编辑器——
        // 语法着色/当前行/选中色改由皮肤 editor 段驱动（回落内置 VSCode 2026），须随两信号重设。
        CodeEditorSetup.WireThemeReapply(CodeEditor, () =>
        {
            ApplyEditorTheme();
            ApplyHighlighting();
        });
        this.Loaded += OnLoaded;
    }

    private bool _layoutPersistWired;

    private void OnLoaded(object? sender, RoutedEventArgs e)
    {
        RestoreLayout();
        WireHostWindowClosing();
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

    /// <summary>持久化上下预览/编辑比例（树宽已上移 shell split，不再此处存）。后台线程写文件，Wait 确保关闭前落盘。</summary>
    private void PersistLayout()
    {
        var store = DesktopApp.Services.GetService<IPreferencesStore>();
        if (store is null) return;
        var (previewStar, editorStar) = CollectLayout();
        try
        {
            Task.Run(async () =>
            {
                await store.SetAsync("notes.previewStar", previewStar.ToString("0.###"));
                await store.SetAsync("notes.editorStar", editorStar.ToString("0.###"));
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
            var ps = double.TryParse(store.GetAsync("notes.previewStar").GetAwaiter().GetResult(), out var p) ? p : 1d;
            var es = double.TryParse(store.GetAsync("notes.editorStar").GetAwaiter().GetResult(), out var e) ? e : 1d;
            if (ps <= 0) ps = 1d;
            if (es <= 0) es = 1d;
            SplitGrid.RowDefinitions[0].Height = new GridLength(ps, GridUnitType.Star);
            SplitGrid.RowDefinitions[2].Height = new GridLength(es, GridUnitType.Star);
        }
        catch { /* 读取失败用默认分隔 */ }
    }

    /// <summary>采集当前上下分栏比例（供关闭时持久化，隐藏页也不丢）。</summary>
    public (double PreviewStar, double EditorStar) CollectLayout()
        => (SplitGrid.RowDefinitions[0].Height.Value,
            SplitGrid.RowDefinitions[2].Height.Value);

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (_vm != null) _vm.PropertyChanged -= OnVmPropertyChanged;
        _vm = DataContext as NotesViewModel;
        if (_vm != null)
        {
            _vm.PropertyChanged += OnVmPropertyChanged;
            SyncCodeEditor();
        }
    }

    private void OnVmPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_vm == null) return;
        if (e.PropertyName == nameof(NotesViewModel.SelectedNode))
        {
            if (_subscribedNode != null) _subscribedNode.PropertyChanged -= OnNodePropertyChanged;
            _subscribedNode = _vm.SelectedNode;
            if (_subscribedNode != null) _subscribedNode.PropertyChanged += OnNodePropertyChanged;
            CreateEditVmForNodeAsync().SafeFireAndForget();
            SyncCodeEditor();
        }
        else if (e.PropertyName == nameof(NotesViewModel.IsCodeEdit) && _vm.IsCodeEdit)
        {
            SyncCodeEditor();
        }
    }

    private IShellHost? MainVm => (TopLevel.GetTopLevel(this)?.DataContext) as IShellHost;

    /// <summary>选中 form 节点时经工厂注册表创建对应表单 VM；工厂封装查同级 VM + 建编辑 VM + 接
    /// Save/AutoSave/Cancel 事件（含 CodeRule 的 C# body 回填、Knowledge 的 body 重解析）。Note/策略族
    /// （Strategy/CodeStrategy）无工厂 → 字典未命中 → 不建表单（全宽代码编辑器）。新增 form 类型 = 加一个工厂 + 注册。</summary>
    private Dictionary<NoteNodeType, INoteNodeEditVmFactory>? _editVmFactories;
    private async Task CreateEditVmForNodeAsync()
    {
        var node = _vm?.SelectedNode;
        if (node is null || _vm is null) return;
        var main = MainVm;
        if (main is null) return;

        _editVmFactories ??= DesktopApp.Services.GetServices<INoteNodeEditVmFactory>()
            .ToDictionary(f => f.NodeType);
        if (!_editVmFactories.TryGetValue(node.NodeType, out var factory)) return;
        _vm.ActiveEditVm = await factory.CreateAsync(new EditVmContext(node, _vm, main, SyncCodeEditor));
    }

    private void OnNodePropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        // 语法变了 → 重设高亮 + 刷新 markdown 预览栏可见性
        if (e.PropertyName == nameof(NoteNodeVM.Syntax))
            ApplyHighlighting();
        if (e.PropertyName is nameof(NoteNodeVM.Syntax) or nameof(NoteNodeVM.IsMarkdown))
            _vm?.RaiseIsMarkdownNode();
    }

    /// <summary>把节点内容载入编辑器并按语法设高亮。</summary>
    private void SyncCodeEditor()
    {
        if (_vm?.SelectedNode is not { } node) return;
        // Document 在控件模板应用（首次显示）后才创建；未就绪时延迟到下一帧再试。
        if (CodeEditor.Document is null)
        {
            Dispatcher.UIThread.Post(SyncCodeEditor);
            return;
        }
        _syncing = true;
        try { CodeEditor.Text = node.Content ?? ""; }
        finally { _syncing = false; }
        ApplyHighlighting();
        UpdateFoldings();
    }

    private void ApplyHighlighting()
    {
        if (_vm?.SelectedNode is not { } node) return;
        var syntax = (NoteNodeTypeRegistry.Get(node.NodeType).Syntax ?? node.Syntax)?.ToLowerInvariant();
        CodeEditorSetup.ApplyHighlighting(CodeEditor, syntax);
    }

    /// <summary>VSCode 质感：当前行高亮 + 选中色 + 搜索面板。仅 Loaded 调一次（避免重复装 SearchPanel/Folding）。
    /// 共享逻辑见 <see cref="CodeEditorSetup"/>；此处仅安装折叠管理器（实例字段，UpdateFoldings 复用）。</summary>
    private void ConfigureEditorAppearance()
    {
        try
        {
            _foldingManager = FoldingManager.Install(CodeEditor.TextArea);
            CodeEditorSetup.ConfigureAppearance(CodeEditor);
        }
        catch { /* 控件未就绪则跳过 */ }
    }

    /// <summary>编辑器主题色（当前行底 + 选中底/前景）。委托 <see cref="CodeEditorSetup"/>——随 ActualThemeVariant 切。</summary>
    private void ApplyEditorTheme() => CodeEditorSetup.ApplyTheme(CodeEditor);

    private void OnCodeEditorTextChanged(object? sender, EventArgs e)
    {
        if (_syncing) return;
        if (_vm?.SelectedNode is { } node)
            node.Content = CodeEditor.Text ?? "";
        _vm?.MarkDirty(); // 启动延迟自动保存
        // 知识对象：下栏源码改了 body → 实时 reparse 同步到编辑器 VM，刷新 markdown 渲染
        if (_vm?.IsKnowledgeNode == true && _vm.ActiveEditVm is KnowledgeEditViewModel kevm)
        {
            var (redef, _) = _vm.TryParseKnowledge(CodeEditor.Text ?? "");
            if (redef is not null) kevm.SyncBody(redef.Body);
        }
        UpdateFoldings();
    }

    private bool _pasting;

    /// <summary>markdown 节点接管粘贴：剪贴板有图片→存本地+插 markdown 引用；否则文本回退。非 markdown 节点不处理。</summary>
    private void OnCodeEditorPreviewKeyDown(object? sender, KeyEventArgs e)
    {
        if (_vm?.IsMarkdownNode != true || _pasting) return;
        // Ctrl+Shift+P 切仅显示上栏 / 上下双栏（眼睛按钮快捷键）
        if (e.Key == Key.P && (e.KeyModifiers & (KeyModifiers.Control | KeyModifiers.Meta)) != 0
            && (e.KeyModifiers & KeyModifiers.Shift) != 0)
        {
            e.Handled = true;
            _vm.TogglePreviewOnlyCommand.Execute(null);
            return;
        }
        if (e.Key != Key.V || (e.KeyModifiers & (KeyModifiers.Control | KeyModifiers.Meta)) == 0) return;
        e.Handled = true; // 阻断 AvaloniaEdit 默认粘贴，由我们 async 处理
        HandlePasteAsync().SafeFireAndForget();
    }

    private async Task HandlePasteAsync()
    {
        if (_pasting) return;
        _pasting = true;
        try
        {
            var clipboard = TopLevel.GetTopLevel(CodeEditor)?.Clipboard;
            if (clipboard is null) return;
            // 图片（截图等）→ 存本地 + 插 markdown 引用
            var bmp = await clipboard.TryGetBitmapAsync();
            if (bmp is not null) { SaveBitmapAndInsert(bmp); return; }
            // 文本回退：保持 markdown 节点正常粘贴文本
            var text = await clipboard.TryGetTextAsync();
            if (!string.IsNullOrEmpty(text)) InsertText(text);
        }
        finally { _pasting = false; }
    }

    private void SaveBitmapAndInsert(Bitmap bmp)
    {
        var root = _vm?.ImageRoot;
        if (string.IsNullOrEmpty(root)) { if (_vm is not null) _vm.StatusMessage = "未配置图片目录"; return; }
        try
        {
            Directory.CreateDirectory(root);
            var noteId = _vm?.SelectedNode?.Id.ToString("N") ?? "note";
            var fileName = $"{noteId}-{Guid.NewGuid():N}.png";
            bmp.Save(Path.Combine(root, fileName)); // .png 扩展名 → PNG 编码
            InsertText($"![图片]({fileName})");
        }
        catch (Exception ex)
        {
            if (_vm is not null) _vm.StatusMessage = $"图片保存失败：{ex.Message}";
        }
    }

    /// <summary>在选区/光标处插入文本（接管粘贴的插入路径，触发 TextChanged→自动落库）。</summary>
    private void InsertText(string text)
    {
        var area = CodeEditor.TextArea;
        var doc = CodeEditor.Document;
        if (area is null || doc is null) return;
        int off;
        var seg = area.Selection.SurroundingSegment;
        if (seg is { Length: > 0 })
        {
            doc.Replace(seg.Offset, seg.Length, text);
            off = seg.Offset + text.Length;
        }
        else
        {
            off = area.Caret.Offset;
            doc.Insert(off, text);
            off += text.Length;
        }
        area.Caret.Offset = off;
        area.ClearSelection();
        area.Caret.BringCaretToView();
    }

    /// <summary>按语法类型生成折叠区域。委托 <see cref="CodeEditorSetup"/>（大括号语言折叠 {}、SQL 折叠 ()、YAML 折叠 --- 文档段）。</summary>
    private void UpdateFoldings()
    {
        if (_foldingManager is null || _vm?.SelectedNode is not { } node) return;
        var forced = NoteNodeTypeRegistry.Get(node.NodeType).Syntax;
        var syntax = (forced ?? node.Syntax)?.ToLowerInvariant();
        CodeEditorSetup.UpdateFoldings(_foldingManager, CodeEditor.Document.Text, syntax);
    }

    /// <summary>Ctrl+滚轮缩放编辑器字体（VSCode 风，8~48，持久化）。</summary>
    private void OnCodeEditorWheelChanged(object? sender, PointerWheelEventArgs e)
    {
        if ((e.KeyModifiers & KeyModifiers.Control) == 0) return;
        if (_vm is null) return;
        var delta = e.Delta.Y;
        if (delta == 0) return;
        var size = Math.Max(8, Math.Min(48, _vm.EditorFontSize + (delta > 0 ? 1 : -1)));
        if (Math.Abs(size - _vm.EditorFontSize) < 0.01) return;
        _vm.EditorFontSize = size; // 绑定更新 CodeEditor.FontSize，并持久化
        e.Handled = true;
    }

    /// <summary>插入笔记内部链接：弹树选择目标节点，在光标处插入 <c>[标题](note-id:&lt;id&gt;)</c>。
    /// 显示文字=可读标题，链接=权威 id（目标改名不断链）；手写 <c>note:标题</c> 也支持导航。</summary>
    private async void OnInsertNoteLink(object? sender, RoutedEventArgs e)
    {
        if (_vm?.SelectedNode is not { } node) return;
        if (TopLevel.GetTopLevel(this) is not Window owner) return;
        var tree = _vm.GetLinkTargetTree(node.Id);
        var picked = await TreePickerWindow.PickAsync(owner, tree, "选择要链接的笔记");
        if (picked is null) return;
        InsertText($"[{picked.Title}](note-id:{picked.Id:N})");
    }
}

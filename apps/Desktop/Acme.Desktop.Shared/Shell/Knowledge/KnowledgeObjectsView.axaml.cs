using System;
using System.IO;
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
/// 「知识对象」页视图（文档版 <c>.md</c> 知识对象 nav 页）。左 <c>.md</c> 树 + 右上下两栏
///（上=<see cref="DocumentKnowledgeView"/> 表单，下=<see cref="AvaloniaEdit.TextEditor"/> YAML/markdown 源码）。
/// 表单↔源码双向同步照 <see cref="DocumentsEditorView"/> 的 <c>.md</c> 同步范式：
/// View 持 <c>_syncing</c> 守卫——表单→源码（<see cref="KnowledgeObjectsViewModel.KnowledgeSourceUpdated"/>）设
/// <c>CodeEditor.Text</c> 时早返 <c>OnCodeEditorTextChanged</c>；源码→表单（<c>OnCodeEditorTextChanged</c>）调
/// <see cref="KnowledgeObjectsViewModel.SyncFromSource"/>，VM 内 <c>_syncing</c> 不回环。
/// <c>.md</c> 语法=markdown（经 <see cref="SyntaxMap"/>），编辑器配置/高亮/折叠/双配色委托 <see cref="CodeEditorSetup"/>。
/// </summary>
public partial class KnowledgeObjectsView : UserControl
{
    private KnowledgeObjectsViewModel? _vm;
    private bool _syncing;
    private bool _layoutPersistWired;
    private FoldingManager? _foldingManager;

    /// <summary>当前活动文件的语法 key（按活动文件扩展名经 SyntaxMap，供 AvaloniaEdit 高亮/折叠）。
    /// 用 <see cref="KnowledgeObjectsViewModel.ActivePath"/>（活动文件真实路径）而非 SelectedNode——
    /// 新建后直接打开/README 打开时 SelectedNode 与活动文件不一致（如 .lean.cs 按 .cs → csharp，而非目录路径 → markdown）。</summary>
    private string CurrentSyntax()
    {
        var ext = Path.GetExtension(_vm?.ActivePath ?? "");
        return SyntaxMap.SyntaxForExtension(ext) ?? "markdown";
    }

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
        // 主题明暗切换 + 主题 JSON 换肤时重染编辑器（.md=markdown 高亮随两信号重设）。
        CodeEditorSetup.WireThemeReapply(CodeEditor, () =>
        {
            CodeEditorSetup.ApplyTheme(CodeEditor);
            CodeEditorSetup.ApplyHighlighting(CodeEditor, CurrentSyntax());
        });
        this.Loaded += (_, _) =>
        {
            SyncEditor();
            RestoreLayout();
            WireHostWindowClosing();
        };
        // 切走（IsVisible=false）再切回（true）时，GridSplitter 可能基于 XAML 默认 star 重新初始化分栏，
        // 把拖动后的非默认比例冲掉。这里在每次重新可见时从盘恢复（拖动结束已即时写盘，读到的即最后一次拖的位置）。
        PropertyChanged += (_, e) =>
        {
            if (e.Property == IsVisibleProperty && IsVisible) RestoreLayout();
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

    /// <summary>持久化上下分栏比例（ko.previewStar/editorStar）。拖动结束/关窗时调用。
    /// 非阻塞：写文件跑后台线程，UI 线程不 Wait（拖动结束事件里阻塞会冻住输入）。</summary>
    private void PersistLayout()
    {
        var store = DesktopApp.Services.GetService<IPreferencesStore>();
        if (store is null) return;
        var (previewStar, editorStar) = CollectLayout();
        try
        {
            _ = Task.Run(async () =>
            {
                await store.SetAsync("ko.previewStar", previewStar.ToString("0.###"));
                await store.SetAsync("ko.editorStar", editorStar.ToString("0.###"));
            });
        }
        catch { /* 写入失败忽略 */ }
    }

    /// <summary>splitter 拖动结束 → 立即写盘。修复切换导航（切走再切回）时上下分栏比例被重置为默认的问题——
    /// 之前只关窗时写一次，切页过程中拖动后的内存 Star 值未落盘，恢复逻辑（RestoreLayout）读到的还是旧值。</summary>
    private void OnSplitterDragCompleted(object? sender, VectorEventArgs e)
    {
        System.Diagnostics.Trace.WriteLine($"[KO-Layout] DragCompleted row0={EditorGrid.RowDefinitions[0].Height.Value} row2={EditorGrid.RowDefinitions[2].Height.Value}");
        PersistLayout();
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
            System.Diagnostics.Trace.WriteLine($"[KO-Layout] RestoreLayout ps={ps} es={es} currentRow0={EditorGrid.RowDefinitions[0].Height.Value} currentRow2={EditorGrid.RowDefinitions[2].Height.Value}");
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
        CodeEditorSetup.ApplyHighlighting(CodeEditor, CurrentSyntax());
        CodeEditorSetup.UpdateFoldings(_foldingManager, CodeEditor.Document.Text, CurrentSyntax());
    }

    /// <summary>外部（agent/外部编辑器）写盘后当前 .md 透明重载：刷新编辑器 + 重解析表单。
    /// SyncEditor 走 _syncing 不触发表单同步路径，须显式补 SyncFromSource。</summary>
    private void OnKnowledgeExternallyReloaded()
    {
        if (_syncing) return;
        SyncEditor();
        _vm?.SyncFromSource(_vm.CurrentSource);
    }

    private void OnVmPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(KnowledgeObjectsViewModel.ActiveEditVm))
            SyncEditor();
        else if (e.PropertyName == nameof(KnowledgeObjectsViewModel.StatusMessage))
            OnStatusMessageChanged();
    }

    /// <summary>状态条已删——错误/保存反馈改经右上角 toast（IToastService 右下角气泡）。
    /// VM 层 20 处 StatusMessage 赋值语义不变，此处只把「非空」转成 toast；「已保存」等成功反馈也弹（短暂绿泡），空则静默。</summary>
    private void OnStatusMessageChanged()
    {
        var msg = _vm?.StatusMessage;
        if (string.IsNullOrWhiteSpace(msg)) return;
        var kind = IsErrorLike(msg) ? ToastKind.Error : ToastKind.Success;
        (DesktopApp.Services?.GetService<IToastService>())?.Show(msg, kind);
    }

    /// <summary>按消息关键字判定错误（失败/重名/未找到/不存在 → 红泡）；其余（已保存等）→ 绿泡。</summary>
    private static bool IsErrorLike(string msg)
        => msg.Contains("失败") || msg.Contains("不存在") || msg.Contains("未找到")
        || msg.Contains("未打开") || msg.Contains("不可用") || msg.Contains("同名");

    /// <summary>把活动文件源码载入编辑器并按扩展名语法设高亮 + 折叠。
    /// 源码编辑器显隐由 XAML 的 <c>EditorVisible</c>（= 选中文件即显，含代码类型）门控，此处不再动 <c>IsVisible</c>
    ///（旧语义「仅 form 类型有源码」已废——代码类型 Note/CodeStrategy/LeanAlgorithm 也须能打开源码）。
    /// 代码类型无上栏表单（<c>ActiveEditVm=null</c>），仍需填 <c>CurrentSource</c>。</summary>
    private void SyncEditor()
    {
        var vm = _vm;
        if (vm is null) return;
        // Document 在控件模板应用（首次显示）后才创建；未就绪时延迟到下一帧再试。
        if (CodeEditor.Document is null)
        {
            Dispatcher.UIThread.Post(SyncEditor);
            return;
        }
        _syncing = true;
        try { CodeEditor.Text = vm.CurrentSource; }
        finally { _syncing = false; }
        CodeEditorSetup.ApplyHighlighting(CodeEditor, CurrentSyntax());
        CodeEditorSetup.UpdateFoldings(_foldingManager, CodeEditor.Document.Text, CurrentSyntax());
    }

    private void OnCodeEditorTextChanged(object? sender, EventArgs e)
    {
        if (_syncing || _vm is null) return;
        // 源码→表单：用户在下栏编辑源码→更新 VM CurrentSource + 置 dirty + 重解析回填表单（VM _syncing 守卫不回环）
        _vm.SyncFromSource(CodeEditor.Text ?? "");
        CodeEditorSetup.UpdateFoldings(_foldingManager, CodeEditor.Document.Text, CurrentSyntax());
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

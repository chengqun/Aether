using System;
using System.ComponentModel;
using System.IO;
using System.Threading.Tasks;
using Acme.Desktop.Mvvm;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using AvaloniaEdit.Folding;
using Microsoft.Extensions.DependencyInjection;

namespace Acme.Desktop.Shared.Shell;

/// <summary>
/// 「打开文档」页：VSCode 式「打开项目目录 → 左侧文件树 → 右侧多标签编辑器」。
/// 左树（<see cref="FileTree"/>）绑 <see cref="DocumentsViewModel.Roots"/>；点文件节点→读盘→<see cref="DocumentsViewModel.Open"/>。
/// 右编辑器单实例随活动 tab 换内容（镜像 NotesView 单编辑器随选中节点换内容）。
/// 文件夹/文件选择、读取、二进制/大文件检测在此（StorageProvider/IO 是 View 关注点）；保存是 VM 命令。
/// 编辑器配置/高亮/折叠/双配色委托 <see cref="CodeEditorSetup"/>——与 NotesView 共用。
/// 树宽持久化（<c>docs.treeW</c>）镜像 NotesView：Loaded 恢复、宿主窗 Closing 落盘。
/// </summary>
public partial class DocumentsView : UserControl
{
    private DocumentsViewModel? _vm;
    private bool _syncing;
    private FoldingManager? _foldingManager;
    private bool _layoutPersistWired;

    private const long MaxFileBytes = 5 * 1024 * 1024; // 5MB 上限，防超大文件卡编辑器

    public DocumentsView()
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
        this.Loaded += OnLoaded;
    }

    private void OnLoaded(object? sender, RoutedEventArgs e)
    {
        RestoreTreeWidth();
        WireHostWindowClosing();
        SyncEditor();
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (_vm != null) _vm.PropertyChanged -= OnVmPropertyChanged;
        _vm = DataContext as DocumentsViewModel;
        if (_vm != null)
        {
            _vm.PropertyChanged += OnVmPropertyChanged;
            SyncEditor();
        }
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

    // —— 打开项目目录（文件夹选择器 → 扫描文件树）——

    private async void OnOpenFolderClick(object? sender, RoutedEventArgs e)
    {
        if (_vm is null) return;
        var top = TopLevel.GetTopLevel(this);
        if (top is null) return;
        var folders = await top.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "打开项目目录",
            AllowMultiple = false
        });
        if (folders is null || folders.Count == 0) return;
        var path = folders[0].Path.LocalPath;
        await _vm.OpenFolderAsync(path);
    }

    // —— 打开单个文件（文件选择器，补充入口）——

    private async void OnOpenFileClick(object? sender, RoutedEventArgs e)
    {
        if (_vm is null) return;
        var top = TopLevel.GetTopLevel(this);
        if (top is null) return;
        var files = await top.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "打开文件",
            AllowMultiple = true,
            FileTypeFilter = new[]
            {
                new FilePickerFileType("代码与文档")
                {
                    Patterns = new[]
                    {
                        "*.cs","*.py","*.md","*.markdown","*.json","*.sql","*.yaml","*.yml",
                        "*.js","*.mjs","*.ts","*.html","*.htm","*.css","*.xml","*.axaml","*.xaml",
                        "*.csproj","*.java","*.cpp","*.cc","*.cxx","*.hpp","*.hh","*.h","*.vb","*.ps1","*.txt"
                    }
                },
                FilePickerFileTypes.All,
            },
        });
        if (files is null || files.Count == 0) return;
        foreach (var file in files)
            await ReadAndOpenAsync(file.Path.LocalPath);
    }

    // —— 树节点选中：文件→读取并打开标签；文件夹→忽略 ——
    // SelectedItem 双绑已设 VM.SelectedNode；此处读取文件内容（IO/二进制检测在 View）。

    private async void OnTreeSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_vm is null) return;
        if (e.AddedItems.Count == 0) return;
        if (e.AddedItems[0] is not FileNode node) return;
        if (node.IsFolder) return; // 文件夹仅展开/收起，不打开标签
        // 已开则激活、不重复读盘
        if (_vm.ActivateIfOpen(node.Path)) return;
        await ReadAndOpenAsync(node.Path);
    }

    /// <summary>读取磁盘文件（5MB/二进制检测）→ 按扩展名算语法 → 打开标签。</summary>
    private async Task ReadAndOpenAsync(string path)
    {
        if (_vm is null) return;
        try
        {
            var bytes = await File.ReadAllBytesAsync(path);
            // 大文件上限
            if (bytes.Length > MaxFileBytes)
            {
                _vm.StatusMessage = $"跳过 {Path.GetFileName(path)}：超过 5MB 上限";
                return;
            }
            // 二进制检测：前 8KB 出现 NUL 字节视为二进制
            var checkLen = Math.Min(bytes.Length, 8192);
            for (var i = 0; i < checkLen; i++)
            {
                if (bytes[i] == 0)
                {
                    _vm.StatusMessage = $"跳过 {Path.GetFileName(path)}：二进制文件";
                    return;
                }
            }
            var content = System.Text.Encoding.UTF8.GetString(bytes);
            var syntax = CodeEditorSetup.SyntaxForExtension(Path.GetExtension(path));
            _vm.Open(path, content, syntax);
        }
        catch (Exception ex)
        {
            _vm.StatusMessage = $"打开失败：{ex.Message}";
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

    // —— 文件树宽度持久化（镜像 NotesView：Loaded 恢复 / 宿主窗 Closing 落盘）——

    private void WireHostWindowClosing()
    {
        if (_layoutPersistWired) return;
        if (TopLevel.GetTopLevel(this) is Window host)
        {
            host.Closing += (_, _) => PersistTreeWidth();
            _layoutPersistWired = true;
        }
    }

    private void PersistTreeWidth()
    {
        var store = DesktopApp.Services.GetService<IPreferencesStore>();
        if (store is null) return;
        var w = TreeGrid.ColumnDefinitions[0].Width.Value;
        try
        {
            Task.Run(async () => await store.SetAsync("docs.treeW", w.ToString("0.#"))).Wait();
        }
        catch { /* 关闭期写入失败忽略 */ }
    }

    private void RestoreTreeWidth()
    {
        var store = DesktopApp.Services.GetService<IPreferencesStore>();
        if (store is null) return;
        try
        {
            if (double.TryParse(store.GetAsync("docs.treeW").GetAwaiter().GetResult(), out var tw)
                && tw is >= 120 and <= 600)
                TreeGrid.ColumnDefinitions[0].Width = new GridLength(tw);
        }
        catch { /* 读取失败用默认宽 */ }
    }
}

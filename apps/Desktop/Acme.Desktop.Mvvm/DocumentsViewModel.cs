using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.DependencyInjection;

namespace Acme.Desktop.Mvvm;

/// <summary>
/// 「打开文档」页 ViewModel：VSCode 式「打开项目目录 → 左侧文件树 → 点文件在右侧多标签编辑器打开」。
/// UI-agnostic（零 Avalonia）。左侧文件树（<see cref="Roots"/>）递归扫描磁盘目录（过滤 bin/obj/.git 等）；
/// 右侧多标签编辑器（<see cref="Tabs"/>）。文件读取/二进制检测在 View code-behind（含 Avalonia StorageProvider）；
/// VM 持树模型 + 标签模型 + 保存（纯 BCL <see cref="File.WriteAllTextAsync"/>）。这是未来 AI 跨文件编辑的文档模型底座。
/// </summary>
public partial class DocumentsViewModel : ObservableObject
{
    private readonly IServiceProvider _serviceProvider;
    private readonly ICurrentProjectDirectoryProvider _projectDirProvider;
    private bool _loadingPref;

    private FileSystemWatcher? _watcher;
    private CancellationTokenSource? _reloadCts;

    /// <summary>目录树扫描时跳过的子目录（构建产物 / VCS / 依赖缓存，体积大且无编辑价值）。</summary>
    private static readonly HashSet<string> ExcludedDirs = new(StringComparer.OrdinalIgnoreCase)
    {
        "bin", "obj", ".git", ".vs", ".idea", "node_modules", ".next", "dist", "build", "target",
        ".dart_tool", ".gradle", "__pycache__", ".venv", "venv", "publish"
    };

    /// <summary>目录树扫描时跳过的文件。</summary>
    private static readonly HashSet<string> ExcludedFiles = new(StringComparer.OrdinalIgnoreCase)
    {
        ".DS_Store", "Thumbs.db"
    };

    /// <summary>文件树根节点（打开项目目录后填充；空 = 未打开目录）。</summary>
    public ObservableCollection<FileNode> Roots { get; } = new();

    /// <summary>树中选中的节点（双绑 TreeView.SelectedItem）。View 据此：文件→读取并打开标签，文件夹→忽略。</summary>
    [ObservableProperty] private FileNode? _selectedNode;

    /// <summary>当前打开的项目目录绝对路径（null = 未打开）。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ProjectName))]
    [NotifyPropertyChangedFor(nameof(HasProject))]
    private string? _rootPath;

    /// <summary>项目名（目录尾名，显示在树头）。</summary>
    public string ProjectName => string.IsNullOrEmpty(RootPath)
        ? string.Empty
        : System.IO.Path.GetFileName(RootPath.TrimEnd(System.IO.Path.DirectorySeparatorChar, System.IO.Path.AltDirectorySeparatorChar));

    /// <summary>是否已打开项目目录（驱动树头/空态切换）。</summary>
    public bool HasProject => !string.IsNullOrEmpty(RootPath);

    /// <summary>已打开的文档标签。</summary>
    public ObservableCollection<DocumentTab> Tabs { get; } = new();

    /// <summary>某标签被外部（agent/外部编辑器）写盘后静默重载时引发。
    /// 仅 <see cref="DocumentTab.SetContentSilently"/> 路径引发（用户键入不引发）；View 据此对 ActiveTab 重载编辑器，
    /// 避免与用户输入打架（用户键入走 OnCodeEditorTextChanged→tab.Content=，不发此事件）。</summary>
    public event Action<DocumentTab>? TabExternallyReloaded;

    /// <summary>当前活动标签（编辑器显示其内容）。null = 无标签（空态）。</summary>
    [ObservableProperty] private DocumentTab? _activeTab;

    /// <summary>编辑器字体大小（持久化到偏好 <c>docs.editorFontSize</c>，重启保留）。</summary>
    [ObservableProperty] private double _editorFontSize = 14;

    /// <summary>状态消息（保存成功/失败、打开提示等）。</summary>
    [ObservableProperty] private string _statusMessage = string.Empty;

    /// <summary>markdown「仅看解析」模式（眼睛切换）：true=仅上栏预览全屏，false=上下双栏（上预览/下源码）。
    /// 仅活动 tab 是 .md 时有意义；镜像 NotesView 的 IsPreviewOnly。</summary>
    [ObservableProperty] private bool _isPreviewOnly;

    // —— markdown 上下分栏布局派生属性（镜像 NotesView 的 EditorRow/EditorRowSpan/SplitterVisible 体系）——

    /// <summary>活动 tab 是否 markdown（驱动上下分栏 vs 编辑器跨满）。</summary>
    public bool IsMarkdownMode => ActiveTab?.IsMarkdown ?? false;

    /// <summary>眼睛按钮可见（md 文件时）。</summary>
    public bool IsPreviewToggleVisible => IsMarkdownMode;

    /// <summary>上栏预览面板可见（md 文件时始终在）。</summary>
    public bool IsPreviewPaneVisible => IsMarkdownMode;

    /// <summary>编辑器面板可见（非 md 总显示；md 时仅 IsPreviewOnly=false 显示）。</summary>
    public bool IsEditorPaneVisible => !IsMarkdownMode || !IsPreviewOnly;

    /// <summary>上下分隔条可见（md 且非仅预览）。</summary>
    public bool IsSplitterVisible => IsMarkdownMode && !IsPreviewOnly;

    /// <summary>编辑器 Grid.Row（md→下栏 row2；非 md→row0 跨满）。</summary>
    public int EditorRow => IsMarkdownMode ? 2 : 0;

    /// <summary>编辑器 Grid.RowSpan（非 md 跨 3 行满；md 双栏 1 行）。</summary>
    public int EditorRowSpan => (IsMarkdownMode && !IsPreviewOnly) ? 1 : 3;

    /// <summary>预览上栏 Grid.RowSpan（仅预览时跨 3 行满，双栏 1 行）。</summary>
    public int PreviewRowSpan => IsPreviewOnly ? 3 : 1;

    /// <summary>ActiveTab 或 IsPreviewOnly 变化后统一刷新布局派生属性绑定。</summary>
    private void RaiseLayout()
    {
        OnPropertyChanged(nameof(IsMarkdownMode));
        OnPropertyChanged(nameof(IsPreviewToggleVisible));
        OnPropertyChanged(nameof(IsPreviewPaneVisible));
        OnPropertyChanged(nameof(IsEditorPaneVisible));
        OnPropertyChanged(nameof(IsSplitterVisible));
        OnPropertyChanged(nameof(EditorRow));
        OnPropertyChanged(nameof(EditorRowSpan));
        OnPropertyChanged(nameof(PreviewRowSpan));
    }

    public DocumentsViewModel(IServiceProvider serviceProvider,
        ICurrentProjectDirectoryProvider projectDirProvider)
    {
        _serviceProvider = serviceProvider;
        _projectDirProvider = projectDirProvider;
        LoadFontSizeAsync().SafeFireAndForget();
        LoadLastProjectAsync().SafeFireAndForget(); // 恢复上次打开的项目目录（VSCode 式记住工作区）
    }

    partial void OnEditorFontSizeChanged(double value)
    {
        if (!_loadingPref) PersistFontSizeAsync(value).SafeFireAndForget();
    }

    /// <summary>RootPath 变化时回写到 DI holder，让 AI 工具能查到当前打开的项目目录。</summary>
    partial void OnRootPathChanged(string? value)
        => _projectDirProvider.CurrentDirectory = value;

    private async Task PersistFontSizeAsync(double value)
    {
        try
        {
            var store = _serviceProvider.GetService<IPreferencesStore>();
            if (store is not null)
                await store.SetAsync("docs.editorFontSize", value.ToString("0.#"));
        }
        catch { /* 偏好存储失败不影响编辑 */ }
    }

    private async Task LoadFontSizeAsync()
    {
        try
        {
            var store = _serviceProvider.GetService<IPreferencesStore>();
            if (store is not null
                && double.TryParse(await store.GetAsync("docs.editorFontSize"), out var fs)
                && fs is >= 8 and <= 48)
            {
                _loadingPref = true;
                EditorFontSize = fs;
                _loadingPref = false;
            }
        }
        catch { /* 读取失败用默认值 */ }
    }

    /// <summary>打开文件（View 已读好内容、按扩展名算好语法 key）。同路径已开则直接激活不重复加。</summary>
    public void Open(string filePath, string content, string? syntaxKey)
    {
        var existing = Tabs.FirstOrDefault(t =>
            string.Equals(t.FilePath, filePath, StringComparison.OrdinalIgnoreCase));
        if (existing is not null)
        {
            ActiveTab = existing;
            return;
        }
        var tab = new DocumentTab(filePath, content, syntaxKey);
        Tabs.Add(tab);
        ActiveTab = tab;
        StatusMessage = $"已打开 {tab.FileName}";
    }

    /// <summary>树节点选中文件时：若该文件已是打开标签则激活并返回 true；未开返回 false（由 View 读取后 <see cref="Open"/>）。</summary>
    public bool ActivateIfOpen(string filePath)
    {
        var existing = Tabs.FirstOrDefault(t =>
            string.Equals(t.FilePath, filePath, StringComparison.OrdinalIgnoreCase));
        if (existing is not null)
        {
            ActiveTab = existing;
            return true;
        }
        return false;
    }

    /// <summary>打开项目目录：清旧树+旧标签，后台递归扫描构建文件树，回 UI 线程挂根节点并展开。
    /// 成功后持久化路径到偏好（<c>docs.projectPath</c>），app 重启自动恢复——VSCode 式记住上次工作区。</summary>
    public async Task OpenFolderAsync(string path)
    {
        if (!Directory.Exists(path))
        {
            StatusMessage = "目录不存在";
            await ClearPersistedProjectAsync();
            return;
        }
        Roots.Clear();
        Tabs.Clear();
        ActiveTab = null;
        RootPath = path;
        StartWatcher(path);
        StatusMessage = "加载中…";
        try
        {
            // 目录枚举可能较慢（大仓库），丢后台线程；FileNode 对象在后台创建、UI 线程挂集合。
            var root = await Task.Run(() => BuildNode(path, isRoot: true));
            Roots.Add(root);
            root.IsExpanded = true;
            StatusMessage = $"已打开 {ProjectName}";
            await PersistProjectPathAsync(path);
        }
        catch (Exception ex)
        {
            StatusMessage = $"打开目录失败：{ex.Message}";
        }
    }

    /// <summary>关闭项目：清树+清标签+清持久化路径（重启不再恢复）。</summary>
    [RelayCommand]
    private void CloseProject()
    {
        Roots.Clear();
        Tabs.Clear();
        ActiveTab = null;
        RootPath = null;
        StopWatcher();
        StatusMessage = string.Empty;
        ClearPersistedProjectAsync().SafeFireAndForget();
    }

    private async Task PersistProjectPathAsync(string path)
    {
        try
        {
            var store = _serviceProvider.GetService<IPreferencesStore>();
            if (store is not null) await store.SetAsync("docs.projectPath", path);
        }
        catch { /* 偏好存储失败不影响使用 */ }
    }

    private async Task ClearPersistedProjectAsync()
    {
        try
        {
            var store = _serviceProvider.GetService<IPreferencesStore>();
            if (store is not null) await store.SetAsync("docs.projectPath", string.Empty);
        }
        catch { /* 忽略 */ }
    }

    /// <summary>启动期恢复上次打开的项目目录（读 <c>docs.projectPath</c>，存在且目录仍在则重新扫描）。</summary>
    private async Task LoadLastProjectAsync()
    {
        try
        {
            var store = _serviceProvider.GetService<IPreferencesStore>();
            if (store is null) return;
            var path = await store.GetAsync("docs.projectPath");
            if (!string.IsNullOrWhiteSpace(path) && Directory.Exists(path))
                await OpenFolderAsync(path);
        }
        catch { /* 恢复失败不影响使用 */ }
    }

    /// <summary>递归构建文件树节点。文件夹：先子目录后文件，各按名排序；跳过 <see cref="ExcludedDirs"/>/<see cref="ExcludedFiles"/>。</summary>
    private static FileNode BuildNode(string fullPath, bool isRoot)
    {
        var name = isRoot
            ? System.IO.Path.GetFileName(fullPath.TrimEnd(System.IO.Path.DirectorySeparatorChar, System.IO.Path.AltDirectorySeparatorChar))
            : System.IO.Path.GetFileName(fullPath);
        if (string.IsNullOrEmpty(name)) name = fullPath;
        var node = new FileNode(name, fullPath, isFolder: true);
        // 先目录后文件，各按名排序（VSCode 式：文件夹在前）。
        foreach (var dir in Directory.EnumerateDirectories(fullPath)
                     .Where(d => !ExcludedDirs.Contains(System.IO.Path.GetFileName(d)))
                     .OrderBy(d => System.IO.Path.GetFileName(d), StringComparer.OrdinalIgnoreCase))
        {
            try { node.Children.Add(BuildNode(dir, isRoot: false)); }
            catch (UnauthorizedAccessException) { /* 跳过无权限目录 */ }
            catch (IOException) { /* 跳过不可访问目录 */ }
        }
        foreach (var file in Directory.EnumerateFiles(fullPath)
                     .Where(f => !ExcludedFiles.Contains(System.IO.Path.GetFileName(f)))
                     .OrderBy(f => System.IO.Path.GetFileName(f), StringComparer.OrdinalIgnoreCase))
        {
            node.Children.Add(new FileNode(System.IO.Path.GetFileName(file), file, isFolder: false));
        }
        return node;
    }

    private bool CanSave => ActiveTab is not null;

    /// <summary>保存活动标签内容回磁盘。Ctrl+S 绑此命令。</summary>
    [RelayCommand(CanExecute = nameof(CanSave))]
    private async Task SaveActiveAsync()
    {
        if (ActiveTab is not { } tab) return;
        try
        {
            await File.WriteAllTextAsync(tab.FilePath, tab.Content);
            tab.IsDirty = false;
            tab.IsStale = false;
            try { tab.LastDiskWrite = File.GetLastWriteTime(tab.FilePath); }
            catch { /* 忽略 */ }
            StatusMessage = $"已保存 {tab.FileName}";
        }
        catch (Exception ex)
        {
            StatusMessage = $"保存失败：{ex.Message}";
        }
    }

    partial void OnActiveTabChanged(DocumentTab? value)
    {
        SaveActiveCommand.NotifyCanExecuteChanged();
        // 切换标签时重置为双栏（新 md 文件默认上预览/下源码；非 md 则 IsPreviewOnly 无意义）。
        IsPreviewOnly = false;
        RaiseLayout();
    }

    partial void OnIsPreviewOnlyChanged(bool value) => RaiseLayout();

    /// <summary>关闭标签。若关的是活动标签，激活邻居。</summary>
    public void CloseTab(DocumentTab tab)
    {
        var idx = Tabs.IndexOf(tab);
        if (idx < 0) return;
        Tabs.RemoveAt(idx);
        if (ReferenceEquals(ActiveTab, tab))
            ActiveTab = Tabs.Count > 0 ? Tabs[Math.Min(idx, Tabs.Count - 1)] : null;
    }

    // —— 实时重载：FileSystemWatcher 透明捕获 agent/外部编辑器写盘 → 刷新开着该文件的标签 ——
    // 不引入 IFileEditNotifier 跨层通知（ai/ 工具层不可引 apps/）：watcher 对所有盘变更一视同仁，零跨层依赖。
    // 不与用户输入打架：dirty 标签置 IsStale 提示而非覆盖；非 dirty 标签 SetContentSilently（不置 dirty）+ 发事件让 View 重载编辑器。

    /// <summary>启动 <see cref="FileSystemWatcher"/> 递归监听项目目录。文件增删改后去抖重载受影响标签。
    /// 镜像 <see cref="SourceControlViewModel"/> 的 watcher 模式。</summary>
    private void StartWatcher(string root)
    {
        StopWatcher();
        try
        {
            _watcher = new FileSystemWatcher(root)
            {
                IncludeSubdirectories = true,
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName
                              | NotifyFilters.LastWrite | NotifyFilters.CreationTime,
                InternalBufferSize = 16 * 1024,
            };
            _watcher.Changed += (_, e) => ScheduleReload();
            _watcher.Created += (_, e) => ScheduleReload();
            _watcher.Deleted += (_, e) => ScheduleReload();
            _watcher.Renamed += (_, e) => ScheduleReload();
            _watcher.Error += (_, _) => ScheduleReload(); // 缓冲溢出 → 尽力刷一次
            _watcher.EnableRaisingEvents = true;
        }
        catch { /* 无权限/路径无效 → 静默，无自动重载 */ }
    }

    private void StopWatcher()
    {
        if (_watcher is not null)
        {
            try { _watcher.EnableRaisingEvents = false; _watcher.Dispose(); }
            catch { /* 忽略 */ }
            _watcher = null;
        }
        _reloadCts?.Cancel();
        _reloadCts = null;
    }

    /// <summary>去抖 400ms 后重载受影响标签（批量改动只刷一次）。FSW 后台线程触发 → marshal UI 线程。</summary>
    private void ScheduleReload()
    {
        _reloadCts?.Cancel();
        var cts = new CancellationTokenSource();
        _reloadCts = cts;
        _ = Task.Run(async () =>
        {
            try { await Task.Delay(400, cts.Token); }
            catch (TaskCanceledException) { return; }
            if (cts.IsCancellationRequested) return;
            var ui = _serviceProvider.GetService<IUiDispatcher>();
            if (ui is not null) ui.Post(ReloadChangedTabs);
            else ReloadChangedTabs();
        });
    }

    /// <summary>遍历打开的标签，按磁盘 mtime 判定是否需重载。
    /// 非 dirty → SetContentSilently + 发事件（View 重载编辑器）；dirty → IsStale 提示不覆盖；文件已删 → IsStale。</summary>
    private void ReloadChangedTabs()
    {
        foreach (var tab in Tabs)
        {
            DateTime diskWrite;
            try { diskWrite = File.GetLastWriteTime(tab.FilePath); }
            catch { diskWrite = DateTime.MinValue; }

            var exists = File.Exists(tab.FilePath);
            if (!exists)
            {
                if (!tab.IsStale) tab.IsStale = true; // 文件被删 → 提示（不关标签，用户可放弃改动）
                continue;
            }

            if (diskWrite <= tab.LastDiskWrite) continue; // mtime 未变 → 跳过

            tab.LastDiskWrite = diskWrite;
            if (tab.IsDirty)
            {
                tab.IsStale = true; // 用户有未存改动 → 不覆盖，提示磁盘已变
                continue;
            }

            try
            {
                var newContent = File.ReadAllText(tab.FilePath);
                tab.SetContentSilently(newContent); // 不置 dirty
                TabExternallyReloaded?.Invoke(tab); // View 据此对 ActiveTab 重载编辑器
            }
            catch { /* 读失败忽略，下次再试 */ }
        }
    }

    /// <summary>用户主动重载 stale 标签：丢弃本地改动，用盘内容覆盖。</summary>
    [RelayCommand]
    private void ReloadStaleTab(DocumentTab? tab)
    {
        if (tab is null) return;
        try
        {
            tab.SetContentSilently(File.ReadAllText(tab.FilePath));
            tab.IsDirty = false;
            tab.IsStale = false;
            try { tab.LastDiskWrite = File.GetLastWriteTime(tab.FilePath); }
            catch { /* 忽略 */ }
            TabExternallyReloaded?.Invoke(tab);
            if (ReferenceEquals(ActiveTab, tab)) StatusMessage = $"已重载 {tab.FileName}";
        }
        catch (Exception ex)
        {
            StatusMessage = $"重载失败：{ex.Message}";
        }
    }
}

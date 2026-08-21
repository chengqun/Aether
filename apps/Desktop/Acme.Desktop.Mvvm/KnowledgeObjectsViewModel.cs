using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Acme.Desktop.Mvvm.Syntax;
using Acme.Notes.Domain.Knowledge;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.DependencyInjection;

namespace Acme.Desktop.Mvvm;

/// <summary>
/// 「知识对象」页 ViewModel（文档版 <c>.ok</c> 知识对象的一等公民 nav 页）。
/// 左 <c>.ok</c> 树（根=独立知识库目录，跨项目复用，<see cref="IKnowledgeDirectoryProvider"/> 与文档页项目目录解耦）
/// + 右单知识对象上下两栏（上=<see cref="DocumentKnowledgeViewModel"/> 表单，下=YAML/markdown 源码）。
/// 镜像 <see cref="DocumentsViewModel"/> 精简：去多标签/markdown 预览分流，专注 <c>.ok</c> 单对象编辑
///（含目录 README 自动打开——点目录→打开该目录 <c>readme.ok</c>，镜像文档页 <c>OpenFolderReadmeAsync</c>）。
/// 表单↔源码双向同步经 <see cref="DocumentKnowledgeViewModel"/> 的 <c>SourceChanged</c>/<c>SyncFromSource</c>，
/// View 持 <c>_syncing</c> 守卫防回环（照 <c>DocumentsEditorView</c> .ok 同步范式）。<c>.ok</c> 树只列 <c>.ok</c> 文件
/// （跳 bin/obj/.git/符号链接，与 <c>DocumentKnowledgeTool.EnumerateOkFiles</c> 同守卫——用户所见=agent 所见）。
/// </summary>
public partial class KnowledgeObjectsViewModel : ObservableObject
{
    private readonly IServiceProvider _serviceProvider;
    private readonly IKnowledgeDirectoryProvider _knowledgeDirProvider;
    private bool _loadingPref;

    private FileSystemWatcher? _watcher;
    private CancellationTokenSource? _reloadCts;
    private CancellationTokenSource? _treeRefreshCts;

    // —— 活动知识对象（单例语义，无多标签）——
    private string? _activePath;
    private string _currentSource = string.Empty;

    /// <summary>目录树扫描时跳过的子目录（与 <c>DocumentKnowledgeTool._ignoredDirs</c> 同集，保证树=AI 工具所见）。</summary>
    private static readonly HashSet<string> IgnoredDirs = new(StringComparer.OrdinalIgnoreCase)
    {
        "bin", "obj", "node_modules", ".git", ".vs", ".idea", ".vscode",
        "packages", "TestResults", "dist", "build", "target", "__pycache__", ".venv", "venv", ".next", ".nuxt",
    };

    /// <summary>文件树根节点（打开知识库目录后填充；空 = 未打开）。</summary>
    public ObservableCollection<FileNode> Roots { get; } = new();

    /// <summary>树中选中的节点（双绑 TreeView.SelectedItem）。文件夹→打开该目录 README（<c>readme.ok</c>）；<c>.ok</c> 文件→打开。</summary>
    [ObservableProperty] private FileNode? _selectedNode;

    /// <summary>当前打开的知识库目录绝对路径（null = 未打开）。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(RootName))]
    [NotifyPropertyChangedFor(nameof(HasRoot))]
    private string? _rootPath;

    /// <summary>知识库名（目录尾名，显示在树头）。</summary>
    public string RootName => string.IsNullOrEmpty(RootPath)
        ? string.Empty
        : Path.GetFileName(RootPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));

    /// <summary>是否已打开知识库目录（驱动树空态切换）。</summary>
    public bool HasRoot => !string.IsNullOrEmpty(RootPath);

    /// <summary>活动知识对象表单 VM（<c>.ok</c> 打开时建，切走/关掉清；表单↔源码双向同步）。null = 无活动对象。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasActiveKnowledge))]
    private DocumentKnowledgeViewModel? _activeKnowledgeVm;

    /// <summary>当前活动 <c>.ok</c> 源码（下栏编辑器显示的内容）。View 经事件 + <see cref="SyncEditor"/> 读用
    ///（<c>TextEditor.Text</c> 非绑定属性，故不绑 <see cref="CurrentSource"/>；View 直接读此值）。</summary>
    public string CurrentSource => _currentSource;

    /// <summary>是否有活动知识对象（驱动右栏空态切换）。</summary>
    public bool HasActiveKnowledge => ActiveKnowledgeVm is not null;

    /// <summary>活动 <c>.ok</c> 有未存改动（Ctrl+S 落盘后清）。</summary>
    [ObservableProperty] private bool _isDirty;

    /// <summary>编辑器字体大小（持久化 <c>ko.editorFontSize</c>，8–48）。</summary>
    [ObservableProperty] private double _editorFontSize = 14;

    /// <summary>状态消息（打开/保存/失败提示）。</summary>
    [ObservableProperty] private string _statusMessage = string.Empty;

    /// <summary>表单→源码：表单字段变→Serialize 生成新源码→更新 <see cref="CurrentSource"/> 后发此事件，
    /// View 据此设 <c>CodeEditor.Text</c>（<c>_syncing</c> 守卫防 OnCodeEditorTextChanged 回环）。</summary>
    public event Action<string>? KnowledgeSourceUpdated;

    /// <summary>外部（agent/外部编辑器）写盘后当前 <c>.ok</c> 透明重载时引发。View 据此重载编辑器 + 重解析表单（非 dirty 时）。</summary>
    public event Action? KnowledgeExternallyReloaded;

    public KnowledgeObjectsViewModel(IServiceProvider serviceProvider,
        IKnowledgeDirectoryProvider knowledgeDirProvider)
    {
        _serviceProvider = serviceProvider;
        _knowledgeDirProvider = knowledgeDirProvider;
        LoadFontSizeAsync().SafeFireAndForget();
        LoadLastRootAsync().SafeFireAndForget(); // 恢复上次打开的知识库目录
    }

    partial void OnEditorFontSizeChanged(double value)
    {
        if (!_loadingPref) PersistFontSizeAsync(value).SafeFireAndForget();
    }

    /// <summary>RootPath 变化时回写到 DI holder，让 AI 工具（DocumentKnowledgeTool）能查到当前打开的知识库目录。</summary>
    partial void OnRootPathChanged(string? value)
        => _knowledgeDirProvider.CurrentDirectory = value;

    partial void OnActiveKnowledgeVmChanged(DocumentKnowledgeViewModel? value)
        => SaveCommand.NotifyCanExecuteChanged();

    /// <summary>树节点选中：<c>.ok</c> 文件→打开；文件夹→打开该目录 README（<c>readme.ok</c>，无则仅展开/收起由 TreeViewItem 默认处理）。</summary>
    partial void OnSelectedNodeChanged(FileNode? value)
    {
        if (value is null) return;
        if (value.IsFolder)
        {
            OpenFolderReadmeAsync(value.Path).SafeFireAndForget();
            return;
        }
        OpenKnowledgeAsync(value.Path).SafeFireAndForget();
    }

    // —— 字体持久化 ——

    private async Task PersistFontSizeAsync(double value)
    {
        try
        {
            var store = _serviceProvider.GetService<IPreferencesStore>();
            if (store is not null)
                await store.SetAsync("ko.editorFontSize", value.ToString("0.#"));
        }
        catch { /* 偏好存储失败不影响编辑 */ }
    }

    private async Task LoadFontSizeAsync()
    {
        try
        {
            var store = _serviceProvider.GetService<IPreferencesStore>();
            if (store is not null
                && double.TryParse(await store.GetAsync("ko.editorFontSize"), out var fs)
                && fs is >= 8 and <= 48)
            {
                _loadingPref = true;
                EditorFontSize = fs;
                _loadingPref = false;
            }
        }
        catch { /* 读取失败用默认值 */ }
    }

    // —— 打开知识对象（.ok 文件）——

    /// <summary>打开 <c>.ok</c> 文件：读盘原文 → 建 <see cref="DocumentKnowledgeViewModel"/>（图闭包经
    /// <see cref="OkTargetResolver.ResolveTarget"/>，root=知识库目录）→ 载入表单 + 设 <see cref="CurrentSource"/>。
    /// 同路径跳过（树刷新恢复选中不重复读盘）。</summary>
    private async Task OpenKnowledgeAsync(string filePath, CancellationToken ct = default)
    {
        if (string.Equals(filePath, _activePath, StringComparison.OrdinalIgnoreCase)) return;
        if (!File.Exists(filePath)) { StatusMessage = "文件不存在"; return; }
        try
        {
            var content = await File.ReadAllTextAsync(filePath, ct);
            _activePath = filePath;
            _currentSource = content;

            var parser = _serviceProvider.GetRequiredService<IDocumentKnowledgeSourceParser>();
            if (ActiveKnowledgeVm is not null)
                ActiveKnowledgeVm.SourceChanged -= OnKnowledgeSourceChanged;

            // 图 focal 名称=当前 .ok 文件名 stem；resolveTargetDef=link targetId(相对知识库根路径)→读 .ok→(名称,定义)，
            // 供 BuildGraphModel 闭包跟出边（镜像 DocumentsViewModel.OnActiveTabChanged，区别仅 root=知识库目录）。
            var currentFileDir = Path.GetDirectoryName(filePath);
            var name = Path.GetFileNameWithoutExtension(filePath);
            Func<string?, (string Title, KnowledgeObjectDefinition? Def)?> resolveTargetDef =
                targetPath => OkTargetResolver.ResolveTarget(parser, targetPath, RootPath, currentFileDir);

            var kvm = new DocumentKnowledgeViewModel(parser, name, resolveTargetDef);
            kvm.SourceChanged += OnKnowledgeSourceChanged;
            kvm.Load(content);
            ActiveKnowledgeVm = kvm; // raises PropertyChanged → View SyncEditor
            IsDirty = false;
            StatusMessage = string.Empty;
        }
        catch (Exception ex)
        {
            StatusMessage = $"打开失败：{ex.Message}";
        }
    }

    // —— 目录 README 自动解析：点击文件树目录时，自动打开该目录下的 README ——
    // 知识对象页的 README 候选=*.ok（知识对象本身可作目录概览；.md 走文档页，本页无 markdown 预览栏）。
    // 复用 OpenKnowledgeAsync（同路径 _activePath 守卫跳过，无需 ActivateIfOpen——无多标签）。

    /// <summary>点击目录时打开该目录下的 README（无则不打开，仅展开/收起由 TreeViewItem 默认处理）。</summary>
    public async Task OpenFolderReadmeAsync(string folderPath, CancellationToken ct = default)
    {
        var readme = TryFindReadme(folderPath);
        if (readme is null) return;
        await OpenKnowledgeAsync(readme, ct); // 已是活动 .ok 则 _activePath 守卫跳过，不重复读盘
    }

    /// <summary>在目录顶层找 README（stem==readme 大小写不敏感，扩展名 .ok）。
    /// 用 <c>*.ok</c> 模式枚举（与 <see cref="BuildNode"/> 同 glob），保证树中所见=README 所能找到。</summary>
    private static string? TryFindReadme(string folderPath)
    {
        try
        {
            foreach (var file in Directory.EnumerateFiles(folderPath, "*.ok"))
            {
                if (string.Equals(Path.GetFileNameWithoutExtension(file), "readme", StringComparison.OrdinalIgnoreCase))
                    return file;
            }
        }
        catch { /* 目录无权限/不存在 → 不打开 README */ }
        return null;
    }

    /// <summary>表单→源码：表单字段变→Serialize 生成新源码→更新 <see cref="CurrentSource"/> + 置 dirty + 通知 View 刷编辑器。</summary>
    private void OnKnowledgeSourceChanged(string newSource)
    {
        _currentSource = newSource;
        IsDirty = true;
        KnowledgeSourceUpdated?.Invoke(newSource); // View 设 CodeEditor.Text（_syncing 守卫防回环）
    }

    /// <summary>源码→表单：用户在下栏编辑源码后由 View 调，重解析回填表单（<see cref="DocumentKnowledgeViewModel"/>
    /// 的 <c>_syncing</c> 守卫不回环）+ 更新 <see cref="CurrentSource"/> + 置 dirty。</summary>
    public void SyncKnowledgeFromSource(string source)
    {
        _currentSource = source;
        IsDirty = true;
        ActiveKnowledgeVm?.SyncFromSource(source);
    }

    /// <summary>清活动知识对象（不关树）：退订事件 + 清状态。打开新目录/关闭项目时调。</summary>
    private void CloseKnowledge()
    {
        if (ActiveKnowledgeVm is not null)
            ActiveKnowledgeVm.SourceChanged -= OnKnowledgeSourceChanged;
        ActiveKnowledgeVm = null;
        _activePath = null;
        _currentSource = string.Empty;
        IsDirty = false;
    }

    private bool CanSave => _activePath is not null;

    /// <summary>保存当前 <c>.ok</c> 内容回磁盘。Ctrl+S 绑此命令。</summary>
    [RelayCommand(CanExecute = nameof(CanSave))]
    private async Task SaveAsync()
    {
        if (_activePath is not { } path) return;
        try
        {
            await File.WriteAllTextAsync(path, _currentSource);
            IsDirty = false;
            StatusMessage = string.Empty;
        }
        catch (Exception ex)
        {
            StatusMessage = $"保存失败：{ex.Message}";
        }
    }

    // —— 打开/关闭知识库目录 ——

    /// <summary>打开知识库目录：清旧树+旧对象，后台递归扫描构建 <c>.ok</c> 树（只列 .ok，跳构建产物/符号链接），
    /// 回 UI 线程挂根节点并展开。成功后持久化路径到偏好（<c>ko.rootPath</c>），重启自动恢复——VSCode 式记住工作区。</summary>
    public async Task OpenFolderAsync(string path)
    {
        if (!Directory.Exists(path))
        {
            StatusMessage = "目录不存在";
            await ClearPersistedRootAsync();
            return;
        }
        Roots.Clear();
        CloseKnowledge();
        SelectedNode = null;
        RootPath = path;
        StartWatcher(path);
        StatusMessage = "加载中…";
        try
        {
            var root = await Task.Run(() => BuildNode(path, isRoot: true));
            Roots.Add(root);
            root.IsExpanded = true;
            StatusMessage = string.Empty;
            await PersistRootPathAsync(path);
        }
        catch (Exception ex)
        {
            StatusMessage = $"打开目录失败：{ex.Message}";
        }
    }

    /// <summary>关闭知识库：清树+清对象+清持久化路径（重启不再恢复）。</summary>
    [RelayCommand]
    private void CloseProject()
    {
        Roots.Clear();
        CloseKnowledge();
        SelectedNode = null;
        RootPath = null;
        StopWatcher();
        StatusMessage = string.Empty;
        ClearPersistedRootAsync().SafeFireAndForget();
    }

    private async Task PersistRootPathAsync(string path)
    {
        try
        {
            var store = _serviceProvider.GetService<IPreferencesStore>();
            if (store is not null) await store.SetAsync("ko.rootPath", path);
        }
        catch { /* 偏好存储失败不影响使用 */ }
    }

    private async Task ClearPersistedRootAsync()
    {
        try
        {
            var store = _serviceProvider.GetService<IPreferencesStore>();
            if (store is not null) await store.SetAsync("ko.rootPath", string.Empty);
        }
        catch { /* 忽略 */ }
    }

    /// <summary>启动期恢复上次打开的知识库目录（读 <c>ko.rootPath</c>，存在且目录仍在则重新扫描）。</summary>
    private async Task LoadLastRootAsync()
    {
        try
        {
            var store = _serviceProvider.GetService<IPreferencesStore>();
            if (store is null) return;
            var path = await store.GetAsync("ko.rootPath");
            if (!string.IsNullOrWhiteSpace(path) && Directory.Exists(path))
                await OpenFolderAsync(path);
        }
        catch { /* 恢复失败不影响使用 */ }
    }

    // —— .ok 树构建（只列 .ok 文件，跳构建产物/VCS/符号链接）——

    /// <summary>递归构建 <c>.ok</c> 树节点。文件夹：先子目录后文件，各按名排序；跳 <see cref="IgnoredDirs"/>/符号链接。
    /// 文件循环用 <c>*.ok</c> 模式只收知识对象（与 <c>DocumentKnowledgeTool.EnumerateOkFiles</c> 同守卫）。</summary>
    private static FileNode BuildNode(string fullPath, bool isRoot)
    {
        var name = isRoot
            ? Path.GetFileName(fullPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
            : Path.GetFileName(fullPath);
        if (string.IsNullOrEmpty(name)) name = fullPath;
        var node = new FileNode(name, fullPath, isFolder: true);
        // 先目录后文件，各按名排序（VSCode 式：文件夹在前）。
        foreach (var dir in Directory.EnumerateDirectories(fullPath)
                     .Where(d => !IsIgnoredDir(Path.GetFileName(d)) && !IsSymlinkDir(d))
                     .OrderBy(d => Path.GetFileName(d), StringComparer.OrdinalIgnoreCase))
        {
            try { node.Children.Add(BuildNode(dir, isRoot: false)); }
            catch (UnauthorizedAccessException) { /* 跳过无权限目录 */ }
            catch (IOException) { /* 跳过不可访问目录 */ }
        }
        foreach (var file in Directory.EnumerateFiles(fullPath, "*.ok")
                     .OrderBy(f => Path.GetFileName(f), StringComparer.OrdinalIgnoreCase))
        {
            node.Children.Add(new FileNode(Path.GetFileName(file), file, isFolder: false));
        }
        return node;
    }

    private static bool IsIgnoredDir(string dirName)
        => dirName.StartsWith(".") || IgnoredDirs.Contains(dirName);

    private static bool IsSymlinkDir(string fullPath)
    {
        try { return (File.GetAttributes(fullPath) & FileAttributes.ReparsePoint) != 0; }
        catch { return false; }
    }

    // —— 实时重载：FileSystemWatcher 透明捕获 agent/外部编辑器写盘 ——
    // 内容变（Changed）→ 重载活动 .ok（非 dirty 时透明刷新表单+编辑器）；结构变（增删改名）→ 重建树。

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
            // 内容变只刷活动对象；结构变须重建树——否则新建的 .ok 不出现。
            _watcher.Changed += (_, _) => ScheduleReload();
            _watcher.Created += (_, _) => ScheduleTreeRefresh();
            _watcher.Deleted += (_, _) => ScheduleTreeRefresh();
            _watcher.Renamed += (_, _) => ScheduleTreeRefresh();
            _watcher.Error += (_, _) => ScheduleReload(); // 缓冲溢出 → 尽力刷活动对象一次
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
        _treeRefreshCts?.Cancel();
        _treeRefreshCts = null;
    }

    /// <summary>去抖 400ms 后重载活动 <c>.ok</c>（批量改动只刷一次）。FSW 后台线程触发 → marshal UI 线程。</summary>
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
            if (ui is not null) ui.Post(ReloadActiveKnowledge);
            else ReloadActiveKnowledge();
        });
    }

    /// <summary>重载活动 <c>.ok</c>：非 dirty → 读盘新内容→更新 <see cref="CurrentSource"/> + 表单重解析
    ///（<see cref="DocumentKnowledgeViewModel.Load"/> 的 <c>_loading</c> 守卫不触发表单→源码回环）+ 通知 View 刷编辑器。
    /// dirty → 跳过（不覆盖用户未存改动；KO 无 tab/stale UI，静默跳过即可）。</summary>
    private void ReloadActiveKnowledge()
    {
        var path = _activePath;
        var dirty = IsDirty;
        if (path is null) return;
        _ = Task.Run(() =>
        {
            if (!File.Exists(path)) return;
            string content;
            try { content = File.ReadAllText(path); }
            catch { return; }
            var ui = _serviceProvider.GetService<IUiDispatcher>();
            if (ui is not null) ui.Post(() => ApplyReload(content, dirty));
            else ApplyReload(content, dirty);
        });
    }

    private void ApplyReload(string content, bool wasDirty)
    {
        if (wasDirty) return; // 用户有未存改动 → 不覆盖
        _currentSource = content;
        ActiveKnowledgeVm?.Load(content); // 重解析表单（Load 内 _loading 守卫不回环 SourceChanged）
        KnowledgeExternallyReloaded?.Invoke(); // View 重载编辑器
    }

    /// <summary>去抖 400ms 后重建 <c>.ok</c> 树（保留展开态与选中）。结构变更走此路径；镜像
    /// <see cref="DocumentsViewModel.ScheduleTreeRefresh"/>（FileNode 无稳定 Id，按 Path 键控）。</summary>
    private void ScheduleTreeRefresh()
    {
        _treeRefreshCts?.Cancel();
        var cts = new CancellationTokenSource();
        _treeRefreshCts = cts;
        _ = Task.Run(async () =>
        {
            try { await Task.Delay(400, cts.Token); }
            catch (TaskCanceledException) { return; }
            if (cts.IsCancellationRequested) return;
            if (string.IsNullOrEmpty(RootPath)) return;

            var expanded = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var selected = SelectedNode?.Path;
            CaptureExpanded(Roots, expanded);

            FileNode? root;
            try { root = BuildNode(RootPath, isRoot: true); }
            catch { return; }

            if (cts.IsCancellationRequested) return;
            var ui = _serviceProvider.GetService<IUiDispatcher>();
            if (ui is not null) ui.Post(() => ApplyTreeRefresh(root, expanded, selected));
            else ApplyTreeRefresh(root, expanded, selected);
        });
    }

    private void ApplyTreeRefresh(FileNode root, HashSet<string> expanded, string? selected)
    {
        if (Roots.Count == 0) return; // 用户已关闭知识库 → 丢弃本次重建
        RestoreExpanded(root, expanded);
        Roots[0] = root;
        if (selected is not null)
        {
            var found = FindByPath(root, selected);
            if (found is not null) SelectedNode = found;
        }
    }

    private static void CaptureExpanded(IEnumerable<FileNode> nodes, HashSet<string> expanded)
    {
        foreach (var n in nodes)
        {
            if (n.IsFolder && n.IsExpanded) expanded.Add(n.Path);
            if (n.IsFolder) CaptureExpanded(n.Children, expanded);
        }
    }

    private static void RestoreExpanded(FileNode node, HashSet<string> expanded)
    {
        if (!node.IsFolder) return;
        if (expanded.Contains(node.Path)) node.IsExpanded = true;
        foreach (var c in node.Children) RestoreExpanded(c, expanded);
    }

    private static FileNode? FindByPath(FileNode node, string path)
    {
        if (string.Equals(node.Path, path, StringComparison.OrdinalIgnoreCase)) return node;
        if (!node.IsFolder) return null;
        foreach (var c in node.Children)
        {
            var f = FindByPath(c, path);
            if (f is not null) return f;
        }
        return null;
    }
}

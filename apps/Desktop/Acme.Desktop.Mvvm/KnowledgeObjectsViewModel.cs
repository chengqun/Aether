using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
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
/// 文件树 + watcher + 持久化 + 打开/关闭目录模板继承自 <see cref="FileTreeVmBase"/>。
/// </summary>
public partial class KnowledgeObjectsViewModel : FileTreeVmBase
{
    private readonly IKnowledgeDirectoryProvider _knowledgeDirProvider;

    // —— 活动知识对象（单例语义，无多标签）——
    private string? _activePath;
    private string _currentSource = string.Empty;

    /// <summary>目录树扫描时跳过的子目录（与 <c>DocumentKnowledgeTool._ignoredDirs</c> 同集，保证树=AI 工具所见）。</summary>
    private static readonly HashSet<string> IgnoredDirs = new(StringComparer.OrdinalIgnoreCase)
    {
        "bin", "obj", "node_modules", ".git", ".vs", ".idea", ".vscode",
        "packages", "TestResults", "dist", "build", "target", "__pycache__", ".venv", "venv", ".next", ".nuxt",
    };

    protected override string FontSizePrefKey => "ko.editorFontSize";
    protected override string RootPathPrefKey => "ko.rootPath";
    protected override void WriteBackDirectory(string? path) => _knowledgeDirProvider.CurrentDirectory = path;

    /// <summary>知识库名（目录尾名，显示在树头）。</summary>
    public string RootName => string.IsNullOrEmpty(RootPath)
        ? string.Empty
        : Path.GetFileName(RootPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));

    /// <summary>是否已打开知识库目录（驱动树空态切换）。</summary>
    public bool HasRoot => !string.IsNullOrEmpty(RootPath);

    protected override void RaiseRootDependentProperties()
    {
        OnPropertyChanged(nameof(RootName));
        OnPropertyChanged(nameof(HasRoot));
    }

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

    /// <summary>表单→源码：表单字段变→Serialize 生成新源码→更新 <see cref="CurrentSource"/> 后发此事件，
    /// View 据此设 <c>CodeEditor.Text</c>（<c>_syncing</c> 守卫防 OnCodeEditorTextChanged 回环）。</summary>
    public event Action<string>? KnowledgeSourceUpdated;

    /// <summary>外部（agent/外部编辑器）写盘后当前 <c>.ok</c> 透明重载时引发。View 据此重载编辑器 + 重解析表单（非 dirty 时）。</summary>
    public event Action? KnowledgeExternallyReloaded;

    public KnowledgeObjectsViewModel(IServiceProvider serviceProvider,
        IKnowledgeDirectoryProvider knowledgeDirProvider)
        : base(serviceProvider)
    {
        _knowledgeDirProvider = knowledgeDirProvider;
        SourceControl = new KnowledgeSourceControlViewModel(serviceProvider, knowledgeDirProvider);
        LoadLastRootAsync().SafeFireAndForget(); // 恢复上次打开的知识库目录（provider 字段已就绪，无虚方法调用坑）
    }

    partial void OnActiveKnowledgeVmChanged(DocumentKnowledgeViewModel? value)
        => SaveCommand.NotifyCanExecuteChanged();

    /// <summary>知识对象页专属源代码管理 VM（独立知识库目录的 git；镜像 <c>MainViewModelBase.SourceControl</c>）。
    /// 由「知识对象 SCM」独立 nav tab 绑定——左栏 <c>SourceControlPanelView</c> + 右 <c>SourceControlView</c> 复用项目 SCM 视图。</summary>
    public KnowledgeSourceControlViewModel SourceControl { get; }

    /// <summary>树节点选中：<c>.ok</c> 文件→打开；文件夹→打开该目录 README（<c>readme.ok</c>，无则仅展开/收起由 TreeViewItem 默认处理）。</summary>
    protected override void OnSelectedNodeChanged(FileNode? value)
    {
        if (value is null) return;
        if (value.IsFolder)
        {
            OpenFolderReadmeAsync(value.Path).SafeFireAndForget();
            return;
        }
        OpenKnowledgeAsync(value.Path).SafeFireAndForget();
    }

    protected override void ClearActiveContent()
    {
        CloseKnowledge();
        SelectedNode = null;
    }

    protected override void ReloadActiveContent() => ReloadActiveKnowledge();

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

            var parser = ServiceProvider.GetRequiredService<IDocumentKnowledgeSourceParser>();
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

    /// <summary>按路径打开 .ok 知识对象（右键菜单「新建知识对象」/「打开」用）。薄包装 <see cref="OpenKnowledgeAsync"/>。</summary>
    public Task OpenKnowledgeByPathAsync(string filePath, CancellationToken ct = default)
        => OpenKnowledgeAsync(filePath, ct);

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

    /// <summary>关闭知识库（薄包装：<see cref="FileTreeVmBase.CloseDirectoryCore"/>）；[RelayCommand] 留在派生类。</summary>
    [RelayCommand]
    private void CloseProject() => CloseDirectoryCore();

    // —— .ok 树构建（只列 .ok 文件，跳构建产物/VCS/符号链接）——

    /// <summary>递归构建 <c>.ok</c> 树节点。文件夹：先子目录后文件，各按名排序；跳 <see cref="IgnoredDirs"/>/符号链接。
    /// 文件循环用 <c>*.ok</c> 模式只收知识对象（与 <c>DocumentKnowledgeTool.EnumerateOkFiles</c> 同守卫）。</summary>
    protected override FileNode BuildNode(string fullPath, bool isRoot)
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
            // 树里只列 .ok 一种文件，后缀是冗余信息——显示名去 .ok（图标仍按完整名解析知识对象图标）。
            node.Children.Add(new FileNode(Path.GetFileNameWithoutExtension(file), Path.GetFileName(file), file, isFolder: false));
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

    // —— 实时重载：内容变（Changed）→ 重载活动 .ok（非 dirty 时透明刷新表单+编辑器）——
    // 结构变（增删改名）的重建树 + 展开/选中保留由基类 ScheduleTreeRefresh/ApplyTreeRefresh 处理；
    // 本页 ApplyTreeRefresh 不需额外刷新活动对象（OnTreeRefreshed 默认空——与原版一致）。

    /// <summary>重载活动 <c>.ok</c>：非 dirty → 读盘新内容→更新 <see cref="CurrentSource"/> + 表单重解析
    ///（<see cref="DocumentKnowledgeViewModel.Load"/> 的 <c>_loading</c> 守卫不触发表单→源码回环）+ 通知 View 刷编辑器。
    /// dirty → 跳过（不覆盖用户未存改动；KO 无 tab/stale UI，静默跳过即可）。文件 IO 移后台线程。</summary>
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
            var ui = ServiceProvider.GetService<IUiDispatcher>();
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
}

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.Extensions.DependencyInjection;

namespace Acme.Desktop.Mvvm;

/// <summary>
/// 文件树页 VM 基类：收拢 <see cref="DocumentsViewModel"/> 与 <see cref="KnowledgeObjectsViewModel"/> 的共享逻辑
///（FileSystemWatcher 实时重载 + 树重建去抖 + 展开/选中保留 + 字体/路径持久化 + 打开/关闭目录模板）。
/// 两者核心差异（多标签 vs 单对象、markdown 预览 vs .md 表单、BuildNode 谓词、README 扩展名）由派生类提供。
/// 不依赖 <see cref="ICurrentProjectDirectoryProvider"/> 或 <see cref="IKnowledgeDirectoryProvider"/>——目录写回经
/// <see cref="WriteBackDirectory"/> 抽象委托，保持两个目录 provider 单例的刻意分离（SCM/终端/活动页绑项目目录，
/// 文档知识工具绑知识库目录，互不覆盖）。文件树模型用共享 <see cref="FileNode"/>。
/// </summary>
public abstract partial class FileTreeVmBase : ObservableObject
{
    /// <summary>DI 容器（懒解析 IPreferencesStore/IUiDispatcher）。派生类亦可访问。</summary>
    protected readonly IServiceProvider ServiceProvider;

    /// <summary>偏好加载期守卫：程序设 EditorFontSize 时不回写偏好（避免循环）。</summary>
    protected bool LoadingPref;

    private FileSystemWatcher? _watcher;
    private CancellationTokenSource? _reloadCts;
    private CancellationTokenSource? _treeRefreshCts;

    protected FileTreeVmBase(IServiceProvider serviceProvider)
    {
        ServiceProvider = serviceProvider;
        LoadFontSizeAsync().SafeFireAndForget();
        // 注意：LoadLastRootAsync 不在基类构造函数调——它会经 OpenFolderAsync 触发抽象钩子
        //（BuildNode/WriteBackDirectory/ClearActiveContent），此时派生类字段尚未初始化（构造函数虚方法调用坑）。
        // 派生类构造函数在自己的 provider 字段赋值后再调 LoadLastRootAsync().SafeFireAndForget()。
    }

    // —— 派生类提供的差异化钩子 ——

    /// <summary>字体持久化偏好键（如 "docs.editorFontSize" / "ko.editorFontSize"）。</summary>
    protected abstract string FontSizePrefKey { get; }

    /// <summary>目录路径持久化偏好键（如 "docs.projectPath" / "ko.rootPath"）。</summary>
    protected abstract string RootPathPrefKey { get; }

    /// <summary>RootPath 变化时写回 DI holder（让 AI 工具查到当前目录）；null 表示清空。</summary>
    protected abstract void WriteBackDirectory(string? path);

    /// <summary>打开/关闭目录时清活动内容（多标签→清 Tabs/ActiveTab；单对象→CloseKnowledge + 清选中）。</summary>
    protected abstract void ClearActiveContent();

    /// <summary>内容变更（Changed）去抖后重载活动内容（多标签→ReloadChangedTabs；单对象→ReloadActiveKnowledge）。</summary>
    protected abstract void ReloadActiveContent();

    /// <summary>递归构建文件树节点（文件夹在前、按名排序；跳哪些目录/收哪些文件由派生类定）。</summary>
    protected abstract FileNode BuildNode(string fullPath, bool isRoot);

    /// <summary>结构变更重建树应用后的额外刷新钩子（多标签页刷 stale 标签；单对象页默认空）。</summary>
    protected virtual void OnTreeRefreshed() { }

    /// <summary>打开期初始化钩子：<see cref="OpenFolderAsync"/> 在 StartWatcher 后、树构建前调用
    ///（try 内，异常归入打开失败）。派生类可在此做打开期初始化——KOVM 在此计算 labs 分区列表 + 恢复
    /// 持久化分区（须在 BuildNode 前——根层过滤读它）。默认空。
    /// 注意：经派生类构造函数的 LoadLastRootAsync fire-and-forget 链（偏好读同步完成）此钩子可能在
    /// 构造期间执行——覆写只可触声明即初始化的字段（同基类构造函数虚方法坑纪律）。</summary>
    protected virtual Task OnFolderOpeningAsync(string path) => Task.CompletedTask;

    /// <summary>打开完成钩子：树挂根 + 展开后调用（try 内，异常归入打开失败）。KOVM 在此触发当前分区
    /// 落地页（打开资料库/启动恢复即见分区 README）。默认空。经派生 ctor 的 LoadLastRootAsync
    /// fire-and-forget 链，此钩子执行在 Task.Run 构建之后的 continuation——派生字段已就绪，
    /// 仍遵守「只触已初始化状态」纪律。</summary>
    protected virtual Task OnFolderOpenedAsync(string path) => Task.CompletedTask;

    /// <summary>树根解析钩子：OpenFolderAsync 挂根 / ApplyTreeRefresh 换根前调，返回挂进 <see cref="Roots"/>
    /// 的节点列表。默认 [builtRoot]。KOVM 在 labs 分区模式下返回当前分区目录的**子节点**——分区名已在树头
    /// 切换器显示，树内不再重复根层（RootPath 不变）。覆写不应设 IsExpanded——展开由
    /// <see cref="ExpandTreeRootsOnOpen"/>（打开路径）或调用方快照恢复（watcher 路径）决定。</summary>
    protected virtual IReadOnlyList<FileNode> ResolveTreeRoots(FileNode builtRoot) => new[] { builtRoot };

    /// <summary>打开路径是否展开树根。Documents 保持 true（打开即展开根=现行为）；KOVM 分区模式 false
    ///（子节点即顶层、天然可见，预展开会多亮一层孙节点）。watcher 路径不受此影响（按快照恢复用户折叠态）。</summary>
    protected virtual bool ExpandTreeRootsOnOpen => true;

    /// <summary>打开目录进行中（OpenFolderAsync 已清树、后台构建未挂根）。派生类自管重建（KOVM 切分区）与
    /// watcher 应用须让位，防双挂根/旧树迟到覆盖。</summary>
    protected bool IsOpeningTree { get; private set; }

    /// <summary>RootPath 变化后通知派生类依赖 RootPath 的属性（ProjectName/HasProject 或 RootName/HasRoot）。</summary>
    protected virtual void RaiseRootDependentProperties() { }

    /// <summary>选中节点变化钩子（知识对象页：文件→打开 .md、目录→打开 README；文档页默认空——由 view 侧分发）。</summary>
    protected virtual void OnSelectedNodeChanged(FileNode? value) { }

    // —— 共享属性 ——

    /// <summary>文件树根节点（打开目录后填充；空 = 未打开）。</summary>
    public ObservableCollection<FileNode> Roots { get; } = new();

    /// <summary>树中选中节点（双绑 TreeView.SelectedItem）。手写属性（非 [ObservableProperty]）以便派生类
    /// 经 <see cref="OnSelectedNodeChanged"/> 虚方法响应选中变化——源生成器的 partial 钩子无法在派生类实现。</summary>
    public FileNode? SelectedNode
    {
        get => _selectedNode;
        set
        {
            if (SetProperty(ref _selectedNode, value))
                OnSelectedNodeChanged(value);
        }
    }
    private FileNode? _selectedNode;

    /// <summary>当前打开的目录绝对路径（null = 未打开）。</summary>
    [ObservableProperty]
    private string? _rootPath;

    /// <summary>编辑器字体大小（持久化，8–48）。</summary>
    [ObservableProperty]
    private double _editorFontSize = 14;

    /// <summary>状态消息（打开/保存/失败提示）。</summary>
    [ObservableProperty]
    private string _statusMessage = string.Empty;

    partial void OnRootPathChanged(string? value)
    {
        WriteBackDirectory(value);
        RaiseRootDependentProperties();
    }

    partial void OnEditorFontSizeChanged(double value)
    {
        if (!LoadingPref) PersistFontSizeAsync(value).SafeFireAndForget();
    }

    // —— 字体持久化 ——

    private async Task PersistFontSizeAsync(double value)
    {
        try
        {
            var store = ServiceProvider.GetService<IPreferencesStore>();
            if (store is not null)
                await store.SetAsync(FontSizePrefKey, value.ToString("0.#"));
        }
        catch { /* 偏好存储失败不影响编辑 */ }
    }

    private async Task LoadFontSizeAsync()
    {
        try
        {
            var store = ServiceProvider.GetService<IPreferencesStore>();
            if (store is not null
                && double.TryParse(await store.GetAsync(FontSizePrefKey), out var fs)
                && fs is >= 8 and <= 48)
            {
                LoadingPref = true;
                EditorFontSize = fs;
                LoadingPref = false;
            }
        }
        catch { /* 读取失败用默认值 */ }
    }

    // —— 打开/关闭目录（模板方法）——

    /// <summary>打开目录：清旧树+旧内容，后台递归扫描构建文件树，回 UI 线程挂根节点并展开。
    /// 成功后持久化路径，重启自动恢复。清活动内容由 <see cref="ClearActiveContent"/> 提供。</summary>
    public async Task OpenFolderAsync(string path)
    {
        if (!Directory.Exists(path))
        {
            StatusMessage = "目录不存在";
            await ClearPersistedRootAsync();
            return;
        }
        Roots.Clear();
        ClearActiveContent();
        RootPath = path;
        StartWatcher(path);
        StatusMessage = "加载中…";
        IsOpeningTree = true;
        try
        {
            await OnFolderOpeningAsync(path);
            var root = await Task.Run(() => BuildNode(path, isRoot: true));
            var treeRoots = ResolveTreeRoots(root);
            foreach (var t in treeRoots) Roots.Add(t);
            if (ExpandTreeRootsOnOpen)
                foreach (var t in treeRoots) t.IsExpanded = true;
            await OnFolderOpenedAsync(path);
            StatusMessage = string.Empty;
            await PersistRootPathAsync(path);
        }
        catch (Exception ex)
        {
            StatusMessage = $"打开目录失败：{ex.Message}";
        }
        finally
        {
            IsOpeningTree = false;
        }
    }

    /// <summary>关闭目录核心逻辑（清树+清内容+清持久化）。派生类的 CloseProject 命令薄包装调此
    ///（[RelayCommand] 留在派生类——源生成器在基类生成的命令继承给派生类的 XAML 绑定不够可靠）。</summary>
    protected void CloseDirectoryCore()
    {
        Roots.Clear();
        ClearActiveContent();
        RootPath = null;
        StopWatcher();
        StatusMessage = string.Empty;
        ClearPersistedRootAsync().SafeFireAndForget();
    }

    // —— 路径持久化 ——

    private async Task PersistRootPathAsync(string path)
    {
        try
        {
            var store = ServiceProvider.GetService<IPreferencesStore>();
            if (store is not null) await store.SetAsync(RootPathPrefKey, path);
        }
        catch { /* 偏好存储失败不影响使用 */ }
    }

    private async Task ClearPersistedRootAsync()
    {
        try
        {
            var store = ServiceProvider.GetService<IPreferencesStore>();
            if (store is not null) await store.SetAsync(RootPathPrefKey, string.Empty);
        }
        catch { /* 忽略 */ }
    }

    /// <summary>启动期恢复上次打开的目录（存在且目录仍在则重新扫描）。派生类构造函数在 provider 字段
    /// 赋值后调此（fire-and-forget）——基类构造函数不调，因其经 OpenFolderAsync 触发抽象钩子，
    /// 此时派生字段尚未初始化。</summary>
    protected async Task LoadLastRootAsync()
    {
        try
        {
            var store = ServiceProvider.GetService<IPreferencesStore>();
            if (store is null) return;
            var path = await store.GetAsync(RootPathPrefKey);
            if (!string.IsNullOrWhiteSpace(path) && Directory.Exists(path))
                await OpenFolderAsync(path);
        }
        catch { /* 恢复失败不影响使用 */ }
    }

    // —— 实时重载：FileSystemWatcher 透明捕获 agent/外部编辑器写盘 ——
    // 内容变（Changed）→ 去抖重载活动内容；结构变（增删改名）→ 去抖重建树（否则新文件不出现）。

    /// <summary>启动 FileSystemWatcher 递归监听目录。</summary>
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
            _watcher.Changed += (_, _) => ScheduleReload();
            _watcher.Created += (_, _) => ScheduleTreeRefresh();
            _watcher.Deleted += (_, _) => ScheduleTreeRefresh();
            _watcher.Renamed += (_, _) => ScheduleTreeRefresh();
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
        _treeRefreshCts?.Cancel();
        _treeRefreshCts = null;
    }

    /// <summary>取消挂起的树重建（400ms 去抖等待中/后台构建中的）。派生类自管重建（如 KOVM 切分区）前调，
    /// 防旧状态构建的树在自管重建应用后迟到覆盖。<see cref="StopWatcher"/> 亦会取消。</summary>
    protected void CancelPendingTreeRefresh()
    {
        _treeRefreshCts?.Cancel();
        _treeRefreshCts = null;
    }

    /// <summary>去抖 400ms 后重载活动内容（批量改动只刷一次）。FSW 后台线程 → marshal UI 线程。</summary>
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
            var ui = ServiceProvider.GetService<IUiDispatcher>();
            if (ui is not null) ui.Post(ReloadActiveContent);
            else ReloadActiveContent();
        });
    }

    /// <summary>去抖 400ms 后重建文件树（保留展开态与选中）。结构变更走此路径；FileNode 无稳定 Id，按 Path 键控。</summary>
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
            var rootPath = RootPath; // 快照本次重建对应的根（应用时比对，关/换根丢弃）

            // 先在调用线程快照展开态/选中（Path 是不可变值，线程安全）。
            var expanded = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var selected = SelectedNode?.Path;
            CaptureExpanded(Roots, expanded);

            FileNode? root;
            try { root = BuildNode(RootPath, isRoot: true); }
            catch { return; /* 重建失败忽略，下次再试 */ }

            if (cts.IsCancellationRequested) return;
            var ui = ServiceProvider.GetService<IUiDispatcher>();
            // Post 闭包内复查取消——关掉「检查→Post」之间的抢占窗口（派生类切分区重建后，旧树不应迟到落地）。
            if (ui is not null) ui.Post(() => { if (!cts.IsCancellationRequested) ApplyTreeRefresh(root, expanded, selected, rootPath); });
            else if (!cts.IsCancellationRequested) ApplyTreeRefresh(root, expanded, selected, rootPath);
        });
    }

    /// <summary>UI 线程：替换树 + 恢复展开态 + 恢复选中 + 派生额外刷新（<see cref="OnTreeRefreshed"/>）。
    /// 丢弃守卫用 rootPath 比对 + <see cref="IsOpeningTree"/>（而非 Roots.Count——空分区挂空树是合法状态，
    /// 不能当作「已关闭」丢弃，否则空分区永远等不到 watcher 刷新）。</summary>
    private void ApplyTreeRefresh(FileNode root, HashSet<string> expanded, string? selected, string rootPath)
    {
        if (IsOpeningTree) return; // 打开进行中 → 让位（打开路径稍后挂新树）
        if (!string.Equals(RootPath, rootPath, StringComparison.OrdinalIgnoreCase)) return; // 已关闭/换根 → 丢弃
        var treeRoots = ResolveTreeRoots(root);
        Roots.Clear();
        foreach (var t in treeRoots)
        {
            RestoreExpanded(t, expanded); // 快照路径在返回子树内仍命中（Path 键控）
            Roots.Add(t);
        }
        if (selected is not null)
        {
            foreach (var t in treeRoots)
            {
                var found = FindByPath(t, selected);
                if (found is not null) { SelectedNode = found; break; }
            }
        }
        OnTreeRefreshed();
    }

    // —— 树展开/选中恢复纯函数（按 FileNode.Path 键控）——

    /// <summary>递归收集展开文件夹的磁盘路径（用于重建后恢复展开态）。</summary>
    protected static void CaptureExpanded(IEnumerable<FileNode> nodes, HashSet<string> expanded)
    {
        foreach (var n in nodes)
        {
            if (n.IsFolder && n.IsExpanded) expanded.Add(n.Path);
            if (n.IsFolder) CaptureExpanded(n.Children, expanded);
        }
    }

    /// <summary>递归恢复展开态（按路径匹配，仅展开原已展开的文件夹）。</summary>
    protected static void RestoreExpanded(FileNode node, HashSet<string> expanded)
    {
        if (!node.IsFolder) return;
        if (expanded.Contains(node.Path)) node.IsExpanded = true;
        foreach (var c in node.Children) RestoreExpanded(c, expanded);
    }

    /// <summary>递归按路径查找节点（恢复选中）。</summary>
    protected static FileNode? FindByPath(FileNode node, string path)
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

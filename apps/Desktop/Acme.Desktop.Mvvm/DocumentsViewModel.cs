using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Acme.Desktop.Mvvm.Syntax;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.DependencyInjection;

namespace Acme.Desktop.Mvvm;

/// <summary>
/// 「打开文档」页 ViewModel：VSCode 式「打开项目目录 → 左侧文件树 → 点文件在右侧多标签编辑器打开」。
/// UI-agnostic（零 Avalonia）。左侧文件树 + 实时重载 + 持久化 + 打开/关闭目录模板继承自 <see cref="FileTreeVmBase"/>；
/// 本类专注文档页差异：多标签编辑器（<see cref="Tabs"/>）、markdown 上下分栏预览、5MB/二进制检测读盘、
/// 目录 README（.md/.markdown）自动打开。文件读 IO 在 VM（供拆分后的独立树控件跨 view 复用）。
/// </summary>
public partial class DocumentsViewModel : FileTreeVmBase
{
    private readonly ICurrentProjectDirectoryProvider _projectDirProvider;

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

    protected override string FontSizePrefKey => "docs.editorFontSize";
    protected override string RootPathPrefKey => "docs.projectPath";
    protected override void WriteBackDirectory(string? path) => _projectDirProvider.CurrentDirectory = path;

    /// <summary>项目名（目录尾名，显示在树头）。</summary>
    public string ProjectName => string.IsNullOrEmpty(RootPath)
        ? string.Empty
        : Path.GetFileName(RootPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));

    /// <summary>是否已打开项目目录（驱动树头/空态切换）。</summary>
    public bool HasProject => !string.IsNullOrEmpty(RootPath);

    protected override void RaiseRootDependentProperties()
    {
        OnPropertyChanged(nameof(ProjectName));
        OnPropertyChanged(nameof(HasProject));
    }

    /// <summary>已打开的文档标签。</summary>
    public ObservableCollection<DocumentTab> Tabs { get; } = new();

    /// <summary>某标签被外部（agent/外部编辑器）写盘后静默重载时引发。
    /// 仅 <see cref="DocumentTab.SetContentSilently"/> 路径引发（用户键入不引发）；View 据此对 ActiveTab 重载编辑器，
    /// 避免与用户输入打架（用户键入走 OnCodeEditorTextChanged→tab.Content=，不发此事件）。</summary>
    public event Action<DocumentTab>? TabExternallyReloaded;

    /// <summary>当前活动标签（编辑器显示其内容）。null = 无标签（空态）。</summary>
    [ObservableProperty] private DocumentTab? _activeTab;

    /// <summary>markdown「仅看解析」模式（眼睛切换）：true=仅上栏预览全屏，false=上下双栏（上预览/下源码）。
    /// 仅活动 tab 是 .md 时有意义；镜像 NotesView 的 IsPreviewOnly。</summary>
    [ObservableProperty] private bool _isPreviewOnly;

    // —— markdown 上下分栏布局派生属性（镜像 NotesView 的 EditorRow/EditorRowSpan/SplitterVisible 体系）——
    // md 双栏：上=预览，下=源码；非 md 编辑器跨满。.md 的 SyntaxKey 也映射 markdown，故与 .md 同走预览/源码两栏。

    /// <summary>活动 tab 是否 markdown（驱动上下分栏 vs 编辑器跨满）。</summary>
    public bool IsMarkdownMode => ActiveTab?.IsMarkdown ?? false;

    /// <summary>眼睛按钮可见（仅 md 文件）。</summary>
    public bool IsPreviewToggleVisible => IsMarkdownMode;

    /// <summary>上栏面板可见（md 预览时显示）。</summary>
    public bool IsPreviewPaneVisible => IsMarkdownMode;

    /// <summary>编辑器面板可见（非 md 总显示；md 时仅 IsPreviewOnly=false）。</summary>
    public bool IsEditorPaneVisible => !IsMarkdownMode || !IsPreviewOnly;

    /// <summary>上下分隔条可见（md 双栏时显示）。</summary>
    public bool IsSplitterVisible => IsMarkdownMode && !IsPreviewOnly;

    /// <summary>编辑器 Grid.Row（md→下栏 row2；其它→row0 跨满）。</summary>
    public int EditorRow => IsMarkdownMode ? 2 : 0;

    /// <summary>编辑器 Grid.RowSpan（其它跨 3 行满；md 双栏 1 行）。</summary>
    public int EditorRowSpan => (IsMarkdownMode && !IsPreviewOnly) ? 1 : 3;

    /// <summary>预览上栏 Grid.RowSpan（md 仅预览时跨 3 行满，其它 1 行）。</summary>
    public int PreviewRowSpan => (IsMarkdownMode && IsPreviewOnly) ? 3 : 1;

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
        : base(serviceProvider)
    {
        _projectDirProvider = projectDirProvider;
        LoadLastRootAsync().SafeFireAndForget(); // 恢复上次打开的项目目录（VSCode 式记住工作区）
    }

    protected override void ClearActiveContent()
    {
        Tabs.Clear();
        ActiveTab = null;
    }

    protected override void ReloadActiveContent() => ReloadChangedTabs();

    protected override void OnTreeRefreshed() => ReloadChangedTabs();

    partial void OnActiveTabChanged(DocumentTab? value)
    {
        SaveActiveCommand.NotifyCanExecuteChanged();
        // IsPreviewOnly 不随 tab 切换重置——「仅预览 / 双栏」是浏览模式（阅读 vs 编辑），
        // 切到别的 md tab 保持上次的模式，浏览多个文档不用反复点眼睛。非 md 文件 IsPreviewOnly 无意义（按钮隐）。
        RaiseLayout();
    }

    partial void OnIsPreviewOnlyChanged(bool value) => RaiseLayout();

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
        StatusMessage = string.Empty; // 成功操作不在标题栏显示确认日志（仅错误/失败时显示）
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

    /// <summary>5MB 上限，防超大文件卡编辑器（原在 View code-behind，迁入 VM 供拆分后的独立树控件复用）。</summary>
    private const long MaxFileBytes = 5 * 1024 * 1024;

    /// <summary>读取磁盘文件（5MB/二进制检测）→ 按扩展名算语法 key（<see cref="SyntaxMap"/>）→ 打开标签。
    /// 原 View code-behind 的 ReadAndOpenAsync 迁入 VM：拆分后文件树是独立 view，文件读 IO 必须在 VM 才能跨 view 复用。
    /// VM 本就持 <see cref="FileSystemWatcher"/> 重载（读盘能力已在），IO 移入无架构越界。</summary>
    public async Task OpenFromDiskAsync(string filePath, CancellationToken ct = default)
    {
        try
        {
            var bytes = await File.ReadAllBytesAsync(filePath, ct);
            if (bytes.Length > MaxFileBytes)
            {
                StatusMessage = $"跳过 {Path.GetFileName(filePath)}：超过 5MB 上限";
                return;
            }
            // 二进制检测：前 8KB 出现 NUL 字节视为二进制（字节版，比解码后查 '\0' 更早拦截）。
            var checkLen = Math.Min(bytes.Length, 8192);
            for (var i = 0; i < checkLen; i++)
            {
                if (bytes[i] == 0)
                {
                    StatusMessage = $"跳过 {Path.GetFileName(filePath)}：二进制文件";
                    return;
                }
            }
            var content = System.Text.Encoding.UTF8.GetString(bytes);
            var syntax = SyntaxMap.SyntaxForExtension(Path.GetExtension(filePath));
            Open(filePath, content, syntax);
        }
        catch (OperationCanceledException) { /* 取消不报 */ }
        catch (Exception ex)
        {
            StatusMessage = $"打开失败：{ex.Message}";
        }
    }

    // —— 目录 README 自动解析：点击文件树目录时，自动打开该目录下的 README ——
    // 按 README 候选扩展名优先级取首个（.md > .markdown）。.md 知识对象有专门的「知识对象」nav 页，
    // 文档页只解析常规 markdown README。

    /// <summary>README 候选扩展名（索引即优先级：.md > .markdown）。</summary>
    private static readonly string[] ReadmeExtensions = { ".md", ".markdown" };

    /// <summary>点击目录时打开该目录下的 README（无则不打开，仅展开/收起由 TreeViewItem 默认处理）。</summary>
    public async Task OpenFolderReadmeAsync(string folderPath, CancellationToken ct = default)
    {
        var readme = TryFindReadme(folderPath);
        if (readme is null) return;
        if (ActivateIfOpen(readme)) return; // 已开则激活不重复读盘
        await OpenFromDiskAsync(readme, ct); // markdown 预览/源码两栏
    }

    /// <summary>在目录顶层找 README（stem==readme，大小写不敏感），按扩展名优先级取首个。</summary>
    private static string? TryFindReadme(string folderPath)
    {
        try
        {
            // 只看顶层文件（不递归子目录）；EnumerateFiles 出错（无权限等）返 null 优雅失败
            foreach (var ext in ReadmeExtensions)
            {
                foreach (var file in Directory.EnumerateFiles(folderPath))
                {
                    var name = Path.GetFileName(file);
                    if (string.Equals(Path.GetFileNameWithoutExtension(name), "readme", StringComparison.OrdinalIgnoreCase)
                        && string.Equals(Path.GetExtension(name), ext, StringComparison.OrdinalIgnoreCase))
                        return file;
                }
            }
        }
        catch { /* 目录无权限/不存在 → 不打开 README */ }
        return null;
    }

    /// <summary>关闭项目（薄包装：<see cref="FileTreeVmBase.CloseDirectoryCore"/>）；[RelayCommand] 留在派生类。</summary>
    [RelayCommand]
    private void CloseProject() => CloseDirectoryCore();

    /// <summary>递归构建文件树节点。文件夹：先子目录后文件，各按名排序；跳过 <see cref="ExcludedDirs"/>/<see cref="ExcludedFiles"/>。</summary>
    protected override FileNode BuildNode(string fullPath, bool isRoot)
    {
        var name = isRoot
            ? Path.GetFileName(fullPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
            : Path.GetFileName(fullPath);
        if (string.IsNullOrEmpty(name)) name = fullPath;
        var node = new FileNode(name, fullPath, isFolder: true);
        // 先目录后文件，各按名排序（VSCode 式：文件夹在前）。
        foreach (var dir in Directory.EnumerateDirectories(fullPath)
                     .Where(d => !ExcludedDirs.Contains(Path.GetFileName(d)))
                     .OrderBy(d => Path.GetFileName(d), StringComparer.OrdinalIgnoreCase))
        {
            try { node.Children.Add(BuildNode(dir, isRoot: false)); }
            catch (UnauthorizedAccessException) { /* 跳过无权限目录 */ }
            catch (IOException) { /* 跳过不可访问目录 */ }
        }
        foreach (var file in Directory.EnumerateFiles(fullPath)
                     .Where(f => !ExcludedFiles.Contains(Path.GetFileName(f)))
                     .OrderBy(f => Path.GetFileName(f), StringComparer.OrdinalIgnoreCase))
        {
            node.Children.Add(new FileNode(Path.GetFileName(file), file, isFolder: false));
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
            StatusMessage = string.Empty;
        }
        catch (Exception ex)
        {
            StatusMessage = $"保存失败：{ex.Message}";
        }
    }

    /// <summary>关闭标签。若关的是活动标签，激活邻居。</summary>
    public void CloseTab(DocumentTab tab)
    {
        var idx = Tabs.IndexOf(tab);
        if (idx < 0) return;
        Tabs.RemoveAt(idx);
        if (ReferenceEquals(ActiveTab, tab))
            ActiveTab = Tabs.Count > 0 ? Tabs[Math.Min(idx, Tabs.Count - 1)] : null;
    }

    /// <summary>遍历打开的标签，按磁盘 mtime 判定是否需重载。
    /// 非 dirty → SetContentSilently + 发事件（View 重载编辑器）；dirty → IsStale 提示不覆盖；文件已删 → IsStale。
    /// 所有文件 IO（GetLastWriteTime/Exists/ReadAllText）移到后台线程——agent 批量写文件时 watcher 触发频繁，
    /// 旧版在 UI 线程同步读盘会与 token flush 争 UI 线程导致卡顿。</summary>
    private void ReloadChangedTabs()
    {
        // 先在 UI 线程快照标签状态（IsDirty/LastDiskWrite 是 UI 绑定属性，避免后台跨线程读）。
        var snapshot = Tabs.Select(t => (Tab: t, t.FilePath, t.IsDirty, t.LastDiskWrite)).ToList();
        _ = Task.Run(() =>
        {
            // 后台线程做所有文件 IO，收集需在 UI 线程应用的数据绑定变更。
            var actions = new List<Action>();
            foreach (var (tab, filePath, isDirty, lastDiskWrite) in snapshot)
            {
                DateTime diskWrite;
                try { diskWrite = File.GetLastWriteTime(filePath); }
                catch { diskWrite = DateTime.MinValue; }

                var exists = File.Exists(filePath);
                if (!exists)
                {
                    var tRef = tab;
                    actions.Add(() => { if (!tRef.IsStale) tRef.IsStale = true; }); // 文件被删 → 提示
                    continue;
                }

                if (diskWrite <= lastDiskWrite) continue; // mtime 未变 → 跳过

                if (isDirty)
                {
                    var tRef = tab;
                    actions.Add(() => { tRef.LastDiskWrite = diskWrite; tRef.IsStale = true; }); // 用户有未存改动 → 不覆盖
                    continue;
                }

                try
                {
                    var newContent = File.ReadAllText(filePath);
                    var tRef = tab;
                    actions.Add(() =>
                    {
                        tRef.LastDiskWrite = diskWrite;
                        tRef.SetContentSilently(newContent); // 不置 dirty
                        TabExternallyReloaded?.Invoke(tRef); // View 据此对 ActiveTab 重载编辑器
                    });
                }
                catch { /* 读失败忽略，下次再试 */ }
            }

            // 回 UI 线程应用数据绑定变更 + 触发事件。
            if (actions.Count == 0) return;
            var ui = ServiceProvider.GetService<IUiDispatcher>();
            if (ui is not null) ui.Post(() => { foreach (var a in actions) a(); });
            else { foreach (var a in actions) a(); }
        });
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
            if (ReferenceEquals(ActiveTab, tab)) StatusMessage = string.Empty;
        }
        catch (Exception ex)
        {
            StatusMessage = $"重载失败：{ex.Message}";
        }
    }
}

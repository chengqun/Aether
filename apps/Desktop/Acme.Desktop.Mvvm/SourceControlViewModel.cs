using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Acme.Desktop.Mvvm.Git;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.DependencyInjection;

namespace Acme.Desktop.Mvvm;

/// <summary>「源代码管理」页 ViewModel：标准 git 工作流（status / diff / stage / commit / discard）。
/// 读「文档页打开的项目目录」经 <see cref="ICurrentProjectDirectoryProvider"/>（订阅 <see cref="ICurrentProjectDirectoryProvider.DirectoryChanged"/> 响应式切仓库），
/// git 操作封装在 <see cref="IGitService"/>（LibGit2Sharp）。UI-agnostic（零 Avalonia）。
/// 左变更列表 + 右 diff：<see cref="ActiveDiff"/> 非空时视图渲染左右并排 diff。</summary>
public partial class SourceControlViewModel : ObservableObject
{
    private readonly IServiceProvider _serviceProvider;
    private readonly ICurrentProjectDirectoryProvider _projectDirProvider;
    private string? _projectDir;

    // 工作区文件监听：用户改文件（Aether 编辑器或外部工具）后自动刷新变更列表。
    // 去抖 + 噪音过滤（.git/bin/obj/node_modules 等）避免频繁刷新。
    private FileSystemWatcher? _watcher;
    /// <summary>专监 <c>.git/refs/</c>（分支/tag 引用文件）——外部 app 提交/切分支改的是 <c>refs/heads/&lt;branch&gt;</c>，
    /// 工作区文件不一定变 + 主 watcher 过滤 .git → 外部提交检测不到。此处监引用文件变即触发刷新。</summary>
    private FileSystemWatcher? _refsWatcher;
    private CancellationTokenSource? _debounceCts;

    public SourceControlViewModel(IServiceProvider serviceProvider, ICurrentProjectDirectoryProvider projectDirProvider)
    {
        _serviceProvider = serviceProvider;
        _projectDirProvider = projectDirProvider;
        _projectDirProvider.DirectoryChanged += OnProjectDirChanged;
        // AI 工具（git_commit/git_push/git_pull）在后台改仓库后经此通知刷新——否则 SCM 页停留旧值。
        var gitNotifier = _serviceProvider.GetService<IGitRepositoryNotifier>();
        if (gitNotifier is not null)
            gitNotifier.GitChanged += OnGitChangedExternally;
        // 启动期若文档页已恢复上次项目，holder 已有值 → 立即同步一次。
        if (!string.IsNullOrEmpty(_projectDirProvider.CurrentDirectory))
            SetProjectAsync(_projectDirProvider.CurrentDirectory).SafeFireAndForget();
    }

    /// <summary>AI 工具在后台线程提交/推送/拉取后触发 → marshal 到 UI 线程刷新。</summary>
    private void OnGitChangedExternally()
    {
        var ui = _serviceProvider.GetService<IUiDispatcher>();
        async Task RefreshAllAsync()
        {
            await RefreshAsync();
            // AI 提交/pull 等外部改动后，历史 section 已展开则重载（否则保持旧图，用户看不到新提交）。
            await ReloadHistoryIfExpandedAsync();
        }
        if (ui is not null) ui.Post(() => RefreshAllAsync().SafeFireAndForget());
        else RefreshAllAsync().SafeFireAndForget();
    }

    /// <summary>历史 section 已展开时重载提交历史（提交/pull/外部改动后保持新鲜）。
    /// 折叠时不加载——下次展开由 <see cref="OnIsHistoryExpandedChanged"/> 懒加载。</summary>
    private async Task ReloadHistoryIfExpandedAsync()
    {
        if (IsHistoryExpanded && IsRepository)
            await LoadHistoryAsync();
    }

    /// <summary>未暂存变更（工作区 vs index）。</summary>
    public ObservableCollection<ChangeItem> Changes { get; } = new();

    /// <summary>已暂存变更（index vs HEAD）。</summary>
    public ObservableCollection<ChangeItem> StagedChanges { get; } = new();

    /// <summary>当前展示的 diff（选中变更时填充）；null = 右栏空态。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasActiveDiff))]
    [NotifyPropertyChangedFor(nameof(HasNoDiff))]
    private DiffModel? _activeDiff;

    /// <summary>当前 diff 对应的文件路径（diff 头显示 + 丢弃按钮）。</summary>
    [ObservableProperty] private string? _activeDiffPath;

    [ObservableProperty] private string _commitMessage = string.Empty;

    /// <summary>是否正在用 LLM 生成提交消息（驱动 ✨ 按钮禁用 + 状态提示）。</summary>
    [ObservableProperty] private bool _isGeneratingCommitMessage;

    /// <summary>是否打开了项目目录。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsNotRepository))]
    [NotifyCanExecuteChangedFor(nameof(InitRepositoryCommand))]
    private bool _hasProject;

    /// <summary>打开的项目是否是 git 仓库。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsNotRepository))]
    [NotifyCanExecuteChangedFor(nameof(InitRepositoryCommand))]
    private bool _isRepository;

    /// <summary>项目名（目录尾名）。</summary>
    [ObservableProperty] private string? _projectName;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(InitRepositoryCommand))]
    private bool _isBusy;

    [ObservableProperty] private string? _statusMessage;

    /// <summary>「更改」段是否展开（折叠头）。</summary>
    [ObservableProperty] private bool _isChangesExpanded = true;

    /// <summary>「暂存的更改」段是否展开（折叠头）。</summary>
    [ObservableProperty] private bool _isStagedExpanded = true;

    /// <summary>提交框是否展开（折叠头；收起后只留工具栏 + 折叠条，腾出空间给变更列表）。</summary>
    [ObservableProperty] private bool _isCommitExpanded = true;

    // ── 历史（提交列表 section，与「更改」「暂存的更改」平级）──

    /// <summary>「历史」section 是否展开（首次展开懒加载提交历史）。</summary>
    [ObservableProperty] private bool _isHistoryExpanded;

    /// <summary>当前历史提交列表（驱动 section 渲染）；切仓库/刷新时重载。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasHistory))]
    private CommitGraphModel? _historyGraph;

    /// <summary>历史提交列表（驱动 section 渲染）；每项是 <see cref="CommitHistoryItemViewModel"/>，
    /// 点行 toggle inline 展开/收起改动文件（GitLens 式），点文件 → 右侧 diff。切仓库/刷新时重载。</summary>
    public ObservableCollection<CommitHistoryItemViewModel> CommitHistory { get; } = new();

    /// <summary>选中 commit 某文件的 diff（驱动右侧 DiffView）。GitLens 式：点展开的文件后填充。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasCommitDiff))]
    [NotifyPropertyChangedFor(nameof(HasNoDiff))]
    private DiffModel? _activeCommitDiff;

    [ObservableProperty] private string? _activeCommitDiffPath;

    // ── 远程同步（push/pull/sync）──

    /// <summary>当前分支相对上游的领先/落后状态（本地计算）。null=未开仓库/非仓库。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasUpstream))]
    [NotifyPropertyChangedFor(nameof(Ahead))]
    [NotifyPropertyChangedFor(nameof(Behind))]
    [NotifyPropertyChangedFor(nameof(HasRemoteCommits))]
    [NotifyPropertyChangedFor(nameof(HasRemote))]
    [NotifyPropertyChangedFor(nameof(RemoteUrl))]
    private RemoteStatus? _remoteStatus;

    /// <summary>能否提交：有变更（暂存或未暂存均可——无暂存时自动暂存全部）且消息非空。</summary>
    public bool CanCommit => (StagedChanges.Count > 0 || Changes.Count > 0) && !string.IsNullOrWhiteSpace(CommitMessage);

    /// <summary>当前是否有 diff 展示（驱动右栏 diff/空态切换）。</summary>
    public bool HasActiveDiff => ActiveDiff is not null;

    /// <summary>打开了项目但不是 git 仓库（驱动「非仓库」空态；未开项目时为 false 走另一空态）。</summary>
    public bool IsNotRepository => HasProject && !IsRepository;

    /// <summary>历史图是否已加载。</summary>
    public bool HasHistory => HistoryGraph is not null && HistoryGraph.Commits.Count > 0;

    /// <summary>是否有 commit 文件 diff 展示（驱动右侧 commit 文件 diff 层）。</summary>
    public bool HasCommitDiff => ActiveCommitDiff is not null;

    /// <summary>无任何 diff 展示（驱动右侧空态）。工作区 diff 与 commit 文件 diff 互斥。</summary>
    public bool HasNoDiff => ActiveDiff is null && ActiveCommitDiff is null;

    /// <summary>当前分支是否已设上游跟踪（驱动 push/pull/sync 按钮可用性）。</summary>
    public bool HasUpstream => RemoteStatus?.HasUpstream ?? false;

    /// <summary>本地领先上游的提交数（push 待推送）。</summary>
    public int Ahead => RemoteStatus?.Ahead ?? 0;

    /// <summary>本地落后上游的提交数（pull 待拉取）。</summary>
    public int Behind => RemoteStatus?.Behind ?? 0;

    /// <summary>是否有待推送/待拉取的提交（驱动同步徽标显示）。</summary>
    public bool HasRemoteCommits => Ahead > 0 || Behind > 0;

    /// <summary>当前仓库的远端 URL（origin 优先，回退首个 remote；无 remote 返回 null）。</summary>
    public string? RemoteUrl => RemoteStatus?.RemoteUrl;

    /// <summary>是否已绑定远端（驱动「未绑定远端」hint 显隐）。</summary>
    public bool HasRemote => !string.IsNullOrEmpty(RemoteStatus?.RemoteUrl);

    private bool CanStageAll => Changes.Count > 0;

    partial void OnCommitMessageChanged(string value) => CommitCommand.NotifyCanExecuteChanged();

    partial void OnIsGeneratingCommitMessageChanged(bool value)
    {
        GenerateCommitMessageCommand.NotifyCanExecuteChanged();
        if (value) StatusMessage = "正在生成提交消息…";
    }

    private bool CanGenerateCommitMessage() => IsRepository && !IsBusy && !IsGeneratingCommitMessage;

    /// <summary>✨ 按当前 git 变更（staged 优先，无则全部）的 diff 经 LLM 生成提交消息，填入
    /// <see cref="CommitMessage"/>。不提交、不暂存——只生成文本，用户可编辑后点「提交」。
    /// 无变更 / 无 LLM 时退化提示。</summary>
    [RelayCommand(CanExecute = nameof(CanGenerateCommitMessage))]
    private async Task GenerateCommitMessageAsync()
    {
        var service = _serviceProvider.GetService<ICommitMessageService>();
        if (service is null) { StatusMessage = "生成提交消息功能不可用"; return; }
        IsGeneratingCommitMessage = true;
        try
        {
            var msg = await service.GenerateAsync();
            if (!string.IsNullOrWhiteSpace(msg))
            {
                CommitMessage = msg;
                StatusMessage = "已生成提交消息，可编辑后提交";
            }
            else
            {
                StatusMessage = "生成失败：无变更或 LLM 不可用";
            }
        }
        catch (Exception ex) { StatusMessage = $"生成失败：{ex.Message}"; }
        finally { IsGeneratingCommitMessage = false; }
    }

    [RelayCommand] private void ToggleChanges() => IsChangesExpanded = !IsChangesExpanded;
    [RelayCommand] private void ToggleStaged() => IsStagedExpanded = !IsStagedExpanded;
    [RelayCommand] private void ToggleHistory() => IsHistoryExpanded = !IsHistoryExpanded;
    [RelayCommand] private void ToggleCommit() => IsCommitExpanded = !IsCommitExpanded;

    /// <summary>首次展开「历史」section 时懒加载提交历史。</summary>
    partial void OnIsHistoryExpandedChanged(bool value)
    {
        if (value && IsRepository && HistoryGraph is null)
            LoadHistoryAsync().SafeFireAndForget();
    }

    /// <summary>初始化 git 仓库（仅「非仓库」空态可用）——把当前项目目录变成仓库并重新加载，
    /// 变更列表随即显示全部未跟踪文件（VSCode/Cursor 标准 <c>git init</c>）。默认分支 main。</summary>
    [RelayCommand(CanExecute = nameof(CanInitRepository))]
    private async Task InitRepositoryAsync()
    {
        var git = _serviceProvider.GetService<IGitService>();
        if (git is null || string.IsNullOrEmpty(_projectDir)) return;
        IsBusy = true;
        try
        {
            var ok = await Task.Run(() => git.Init(_projectDir));
            if (ok)
                await SetProjectAsync(_projectDir); // 重载——IsRepository 变 true，变更列表显示未跟踪文件。
            else
                StatusMessage = "初始化失败：无法在该目录创建 git 仓库";
        }
        finally { IsBusy = false; }
    }

    private bool CanInitRepository() => HasProject && !IsRepository && !IsBusy;

    /// <summary>加载提交历史拓扑图（≤200 条）。</summary>
    [RelayCommand]
    private async Task LoadHistoryAsync()
    {
        if (string.IsNullOrEmpty(_projectDir)) return;
        var git = _serviceProvider.GetService<IGitService>();
        if (git is null || !git.IsRepository(_projectDir)) return;
        IsBusy = true;
        try
        {
            var nodes = await Task.Run(() => git.GetHistory(_projectDir, 200));
            HistoryGraph = CommitGraphBuilder.Build(nodes);
            // GitLens 式：每个 commit 包成可 inline 展开改动文件的行 VM；点行 toggle、点文件 → 右侧 diff。
            CommitHistory.Clear();
            foreach (var c in HistoryGraph.Commits)
                CommitHistory.Add(new CommitHistoryItemViewModel(c, LoadCommitFilesAsync, OpenCommitFileDiffAsync));
            // 固定上限 200，显示个数无信息量（小仓库另说）——仅空仓库提示。
            StatusMessage = HistoryGraph.Commits.Count == 0 ? "无提交" : null;
        }
        catch (Exception ex) { StatusMessage = $"加载历史失败：{ex.Message}"; }
        finally { IsBusy = false; }
    }

    /// <summary>加载某 commit 的改动文件列表（GitLens 式 inline 展开时懒加载回调；
    /// 喂 <see cref="CommitHistoryItemViewModel"/>）。后台线程跑 LibGit2Sharp 不阻塞 UI。</summary>
    private async Task<IReadOnlyList<CommitFileChange>> LoadCommitFilesAsync(string sha)
    {
        var git = _serviceProvider.GetService<IGitService>();
        if (git is null || string.IsNullOrEmpty(_projectDir)) return Array.Empty<CommitFileChange>();
        try { return await Task.Run(() => git.GetCommitChanges(_projectDir!, sha)); }
        catch { return Array.Empty<CommitFileChange>(); }
    }

    /// <summary>点 commit 改动文件 → 取该文件 parent-vs-commit diff，右侧显示（清工作区 diff 互斥）。
    /// GitLens 式：sha 由 <see cref="CommitHistoryItemViewModel"/> 注入（它知道自己包的 commit）。</summary>
    private async Task OpenCommitFileDiffAsync(string sha, CommitFileChange file)
    {
        if (string.IsNullOrEmpty(_projectDir)) return;
        var git = _serviceProvider.GetService<IGitService>();
        if (git is null) return;
        IsBusy = true;
        try
        {
            var diff = await Task.Run(() => git.GetCommitFileDiff(_projectDir!, sha, file.Path));
            ActiveDiff = null;          // 与工作区 diff 互斥——右侧只显一个 diff。
            ActiveDiffPath = null;
            ActiveCommitDiff = diff;
            ActiveCommitDiffPath = file.Path;
        }
        catch (Exception ex) { StatusMessage = $"读取差异失败：{ex.Message}"; }
        finally { IsBusy = false; }
    }

    /// <summary>关闭右侧 commit 文件 diff（diff 头 ✕ 按钮），回空态。</summary>
    [RelayCommand]
    private void ClearCommitDiff()
    {
        ActiveCommitDiff = null;
        ActiveCommitDiffPath = null;
    }

    /// <summary>项目目录变化（文档页打开/切换项目）→ 切仓库 + 刷新。</summary>
    private void OnProjectDirChanged(string? dir) => SetProjectAsync(dir).SafeFireAndForget();

    private async Task SetProjectAsync(string? dir)
    {
        // 切项目 → 先停旧监听（避免监听已离开的仓库）。
        StopWatcher();
        _projectDir = dir;
        HasProject = !string.IsNullOrEmpty(dir);
        ProjectName = HasProject ? Path.GetFileName(dir!.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)) : null;
        var git = _serviceProvider.GetService<IGitService>();
        IsRepository = git?.IsRepository(dir ?? string.Empty) ?? false;
        Changes.Clear();
        StagedChanges.Clear();
        ActiveDiff = null;
        ActiveDiffPath = null;
        // 清历史状态（切仓库）。
        HistoryGraph = null;
        CommitHistory.Clear();
        ActiveCommitDiff = null;
        ActiveCommitDiffPath = null;
        RemoteStatus = null;
        if (IsRepository)
        {
            StartWatcher(dir!);
            await RefreshAsync();
            if (IsHistoryExpanded) await LoadHistoryAsync();
        }
    }

    // ── 工作区文件监听（自动刷新变更列表）──

    /// <summary>启动 <see cref="FileSystemWatcher"/> 递归监听仓库工作区。文件增删改后去抖刷新。
    /// 无权限等异常静默——用户仍可手动点刷新。</summary>
    private void StartWatcher(string repoRoot)
    {
        try
        {
            _watcher = new FileSystemWatcher(repoRoot)
            {
                IncludeSubdirectories = true,
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName
                              | NotifyFilters.LastWrite | NotifyFilters.CreationTime,
                InternalBufferSize = 16 * 1024, // 增大缓冲减少 overflow
            };
            _watcher.Changed += (_, e) => OnFileChangedCore(e.FullPath);
            _watcher.Created += (_, e) => OnFileChangedCore(e.FullPath);
            _watcher.Deleted += (_, e) => OnFileChangedCore(e.FullPath);
            _watcher.Renamed += (_, e) => OnFileChangedCore(e.FullPath);
            _watcher.Error += (_, _) => ScheduleRefresh(); // 缓冲溢出等 → 尽力刷一次。
            _watcher.EnableRaisingEvents = true;

            // 单独监 .git/refs/（分支/tag 引用文件）——外部 IDE/终端提交改的是 refs/heads/<branch>，
            // 工作区文件不一定变 + 主 watcher 过滤 .git → 外部提交检测不到。复用 OnGitChangedExternally
            // （RefreshAsync + 历史重载 + UI marshal），让 SCM 页在别的 app 提交后自动刷新。
            var refsDir = Path.Combine(repoRoot, ".git", "refs");
            if (Directory.Exists(refsDir))
            {
                _refsWatcher = new FileSystemWatcher(refsDir)
                {
                    IncludeSubdirectories = true,
                    NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.CreationTime,
                    InternalBufferSize = 8 * 1024,
                };
                _refsWatcher.Changed += (_, _) => OnGitChangedExternally();
                _refsWatcher.Created += (_, _) => OnGitChangedExternally();
                _refsWatcher.Deleted += (_, _) => OnGitChangedExternally();
                _refsWatcher.Renamed += (_, _) => OnGitChangedExternally();
                _refsWatcher.Error += (_, _) => OnGitChangedExternally();
                _refsWatcher.EnableRaisingEvents = true;
            }
        }
        catch { /* 无权限/路径无效 → 静默，手动刷新兜底 */ }
    }

    private void StopWatcher()
    {
        if (_watcher is not null)
        {
            try { _watcher.EnableRaisingEvents = false; _watcher.Dispose(); }
            catch { /* 忽略 */ }
            _watcher = null;
        }
        if (_refsWatcher is not null)
        {
            try { _refsWatcher.EnableRaisingEvents = false; _refsWatcher.Dispose(); }
            catch { /* 忽略 */ }
            _refsWatcher = null;
        }
        _debounceCts?.Cancel();
        _debounceCts = null;
    }

    /// <summary>文件变化回调（FSW 后台线程触发）：噪音过滤 + 去抖后 marshal UI 线程刷新。</summary>
    private void OnFileChangedCore(string fullPath)
    {
        if (IsNoisePath(fullPath)) return;
        ScheduleRefresh();
    }

    /// <summary>去抖 400ms 后刷新（批量改动只刷一次）。</summary>
    private void ScheduleRefresh()
    {
        _debounceCts?.Cancel();
        var cts = new CancellationTokenSource();
        _debounceCts = cts;
        _ = Task.Run(async () =>
        {
            try { await Task.Delay(400, cts.Token); }
            catch (TaskCanceledException) { return; }
            if (cts.IsCancellationRequested) return;
            var ui = _serviceProvider.GetService<IUiDispatcher>();
            if (ui is not null) ui.Post(() => RefreshAsync().SafeFireAndForget());
            else RefreshAsync().SafeFireAndForget();
        });
    }

    /// <summary>高 churn 噪音目录（.git/bin/obj/node_modules/.vs 等）——其变化不反映用户业务改动，忽略。</summary>
    private static bool IsNoisePath(string fullPath)
    {
        var p = fullPath.Replace('\\', '/');
        return p.Contains("/.git/", StringComparison.OrdinalIgnoreCase)
            || p.EndsWith("/.git", StringComparison.OrdinalIgnoreCase)
            || p.Contains("/bin/", StringComparison.OrdinalIgnoreCase)
            || p.Contains("/obj/", StringComparison.OrdinalIgnoreCase)
            || p.Contains("/node_modules/", StringComparison.OrdinalIgnoreCase)
            || p.Contains("/.vs/", StringComparison.OrdinalIgnoreCase);
    }

    [RelayCommand]
    private async Task RefreshAsync()
    {
        if (string.IsNullOrEmpty(_projectDir)) return;
        var git = _serviceProvider.GetService<IGitService>();
        if (git is null || !git.IsRepository(_projectDir)) { IsRepository = false; return; }
        IsRepository = true;
        IsBusy = true;
        try
        {
            var status = await Task.Run(() => git.GetStatus(_projectDir));
            var unstaged = status.Where(c => !c.IsStaged).Select(c => new ChangeItem(c.Path, c.Kind, IsStaged: false)).ToList();
            var staged = status.Where(c => c.IsStaged).Select(c => new ChangeItem(c.Path, c.Kind, IsStaged: true)).ToList();
            Changes.Clear(); foreach (var c in unstaged) Changes.Add(c);
            StagedChanges.Clear(); foreach (var c in staged) StagedChanges.Add(c);
            CommitCommand.NotifyCanExecuteChanged();
            StageAllCommand.NotifyCanExecuteChanged();
            // 若当前 diff 的文件已不在变更列表（已提交/撤销）→ 清 diff。
            if (ActiveDiffPath is not null && !status.Any(c => c.Path == ActiveDiffPath))
            { ActiveDiff = null; ActiveDiffPath = null; }
            StatusMessage = status.Count == 0 ? "无变更" : $"{status.Count} 个变更";
            // 远程领先/落后状态（本地计算，不联网）。
            RemoteStatus = git.GetRemoteStatus(_projectDir);
        }
        catch (Exception ex) { StatusMessage = $"刷新失败：{ex.Message}"; }
        finally { IsBusy = false; }
    }

    /// <summary>点击变更行 → 取 diff 填 <see cref="ActiveDiff"/>。同时清历史选中（切回工作区 diff 层）。</summary>
    [RelayCommand]
    private async Task OpenDiffAsync(ChangeItem? item)
    {
        if (item is null || string.IsNullOrEmpty(_projectDir)) return;
        var git = _serviceProvider.GetService<IGitService>();
        if (git is null) return;
        ActiveCommitDiff = null;       // 切回工作区 diff——清 commit 文件 diff（互斥）。
        ActiveCommitDiffPath = null;
        try
        {
            var diff = await Task.Run(() => git.GetDiff(_projectDir, item.Path));
            ActiveDiff = diff;
            ActiveDiffPath = item.Path;
        }
        catch (Exception ex) { StatusMessage = $"读取差异失败：{ex.Message}"; }
    }

    [RelayCommand]
    private async Task StageAsync(ChangeItem? item)
    {
        if (item is null) return;
        var git = _serviceProvider.GetService<IGitService>();
        if (git is null) return;
        await Task.Run(() => git.Stage(_projectDir!, item.Path));
        await RefreshAsync();
    }

    [RelayCommand(CanExecute = nameof(CanStageAll))]
    private async Task StageAllAsync()
    {
        var git = _serviceProvider.GetService<IGitService>();
        if (git is null || Changes.Count == 0) return;
        foreach (var c in Changes.ToList()) await Task.Run(() => git.Stage(_projectDir!, c.Path));
        await RefreshAsync();
    }

    [RelayCommand]
    private async Task UnstageAsync(ChangeItem? item)
    {
        if (item is null) return;
        var git = _serviceProvider.GetService<IGitService>();
        if (git is null) return;
        await Task.Run(() => git.Unstage(_projectDir!, item.Path));
        await RefreshAsync();
    }

    [RelayCommand]
    private async Task DiscardAsync(ChangeItem? item)
    {
        if (item is null) return;
        var git = _serviceProvider.GetService<IGitService>();
        if (git is null) return;
        await Task.Run(() => git.Discard(_projectDir!, item.Path));
        await RefreshAsync();
    }

    /// <summary>丢弃当前 diff 所在文件的改动（diff 头「丢弃更改」按钮）。</summary>
    [RelayCommand]
    private async Task DiscardActiveAsync()
    {
        if (ActiveDiffPath is null) return;
        var item = Changes.Concat(StagedChanges).FirstOrDefault(c => c.Path == ActiveDiffPath);
        if (item is not null) await DiscardAsync(item);
    }

    // ── 远程同步命令 ──

    /// <summary>推送当前分支到远程（不可逆）。</summary>
    [RelayCommand]
    private async Task PushAsync()
    {
        if (string.IsNullOrEmpty(_projectDir)) return;
        var git = _serviceProvider.GetService<IGitService>();
        if (git is null) return;
        IsBusy = true;
        try
        {
            var (ok, msg) = await Task.Run(() => git.Push(_projectDir));
            StatusMessage = msg;
            await RefreshAsync();
            if (ok) await ReloadHistoryIfExpandedAsync(); // 远端跟踪 ref 变化，历史图徽标刷新。
        }
        catch (Exception ex) { StatusMessage = $"推送失败：{ex.Message}"; }
        finally { IsBusy = false; }
    }

    /// <summary>拉取并合并远程更新到当前分支（可能产生冲突）。</summary>
    [RelayCommand]
    private async Task PullAsync()
    {
        if (string.IsNullOrEmpty(_projectDir)) return;
        var git = _serviceProvider.GetService<IGitService>();
        if (git is null) return;
        IsBusy = true;
        try
        {
            var (ok, msg) = await Task.Run(() => git.Pull(_projectDir));
            StatusMessage = msg;
            await RefreshAsync();
            if (ok) await ReloadHistoryIfExpandedAsync(); // 拉取可能合入新提交，历史图刷新。
        }
        catch (Exception ex) { StatusMessage = $"拉取失败：{ex.Message}"; }
        finally { IsBusy = false; }
    }

    /// <summary>同步：先 pull 再 push（VSCode 式 sync）。</summary>
    [RelayCommand]
    private async Task SyncAsync()
    {
        if (string.IsNullOrEmpty(_projectDir)) return;
        var git = _serviceProvider.GetService<IGitService>();
        if (git is null) return;
        IsBusy = true;
        try
        {
            var (pullOk, pullMsg) = await Task.Run(() => git.Pull(_projectDir));
            if (pullOk)
            {
                var (pushOk, pushMsg) = await Task.Run(() => git.Push(_projectDir));
                StatusMessage = $"pull: {pullMsg} | push: {pushMsg}";
            }
            else
            {
                StatusMessage = pullMsg;
            }
            await RefreshAsync();
            await ReloadHistoryIfExpandedAsync();
        }
        catch (Exception ex) { StatusMessage = $"同步失败：{ex.Message}"; }
        finally { IsBusy = false; }
    }

    /// <summary>打开远程设置对话框（绑定/更换远端 + 凭证诊断；不存 PAT）。传入项目目录，关闭后刷新远程状态。</summary>
    [RelayCommand]
    private async Task OpenRemoteSettingsAsync()
    {
        var dlg = _serviceProvider.GetService<IRemoteSettingsDialogService>();
        if (dlg is null) return;
        await dlg.ShowAsync(_projectDir);
        await RefreshAsync();
    }

    [RelayCommand(CanExecute = nameof(CanCommit))]
    private async Task CommitAsync()
    {
        if (!CanCommit) return;
        var git = _serviceProvider.GetService<IGitService>();
        if (git is null) return;
        try
        {
            // 无暂存项时自动暂存全部（含未跟踪），符合多数 git GUI 直觉——输入消息即可提交全部改动。
            if (StagedChanges.Count == 0)
                foreach (var c in Changes.ToList())
                    await Task.Run(() => git.Stage(_projectDir!, c.Path));
            var ok = await Task.Run(() => git.Commit(_projectDir!, CommitMessage.Trim()));
            StatusMessage = ok ? "已提交" : "提交失败";
            if (ok)
            {
                CommitMessage = string.Empty;
            }
            await RefreshAsync();
            if (ok) await ReloadHistoryIfExpandedAsync(); // 新提交进历史图（展开时即时刷新）。
        }
        catch (Exception ex) { StatusMessage = $"提交失败：{ex.Message}"; }
    }
}

/// <summary>变更列表项（不可变；刷新时整体重建）。UI 据 <see cref="Kind"/> 着状态字母色。</summary>
public sealed record ChangeItem(string Path, ChangeKind Kind, bool IsStaged)
{
    public string FileName => System.IO.Path.GetFileName(Path);
    public string Dir => System.IO.Path.GetDirectoryName(Path) ?? string.Empty;
    public string StatusLetter => Kind switch
    {
        ChangeKind.Modified => "M",
        ChangeKind.Added => "A",
        ChangeKind.Deleted => "D",
        ChangeKind.Untracked => "U",
        ChangeKind.Renamed => "R",
        _ => "?",
    };
}

/// <summary>历史提交行 VM（GitLens 式 inline 展开）：包 <see cref="LaidOutCommit"/> + 可变展开态 + 懒加载改动文件。
/// 点行 toggle 展开/收起（首次展开懒加载文件，缓存复用不重载）；点展开的文件 → 经回调让父
/// <see cref="SourceControlViewModel"/> 取该文件 commit diff 显于右侧。
/// <see cref="LaidOutCommit"/> 是 <see cref="CommitGraphBuilder"/> 产的纯 record 不可变，故包一层 VM 加可观察态。</summary>
public sealed partial class CommitHistoryItemViewModel : ObservableObject
{
    private readonly Func<string, Task<IReadOnlyList<CommitFileChange>>> _loadFiles;
    private readonly Func<string, CommitFileChange, Task> _openFileDiff;

    public LaidOutCommit Commit { get; }
    /// <summary>便利直通（XAML 绑 <c>Info.Message</c>/<c>Info.When</c> 等，免 <c>Commit.Info.X</c>）。</summary>
    public CommitInfo Info => Commit.Info;
    public int Lane => Commit.Lane;
    public IReadOnlyList<CommitRef> Refs => Commit.Refs;

    [ObservableProperty] private bool _isExpanded;
    [ObservableProperty] private bool _isLoading;
    public ObservableCollection<CommitFileChange> Files { get; } = new();

    public CommitHistoryItemViewModel(LaidOutCommit commit,
        Func<string, Task<IReadOnlyList<CommitFileChange>>> loadFiles,
        Func<string, CommitFileChange, Task> openFileDiff)
    {
        Commit = commit;
        _loadFiles = loadFiles;
        _openFileDiff = openFileDiff;
    }

    /// <summary>点 commit 行 → toggle inline 展开/收起；首次展开懒加载改动文件。</summary>
    [RelayCommand]
    private async Task ToggleAsync()
    {
        IsExpanded = !IsExpanded;
        if (IsExpanded && Files.Count == 0 && !IsLoading)
        {
            IsLoading = true;
            try
            {
                var files = await _loadFiles(Info.Sha);
                Files.Clear();
                foreach (var f in files) Files.Add(f);
            }
            finally { IsLoading = false; }
        }
    }

    /// <summary>点展开的改动文件 → 经回调让父 VM 取该文件 commit diff 显于右侧。
    /// sha 由本 VM 注入（它知道自己包的 commit），故 diff 不依赖「选中 commit」单态。</summary>
    [RelayCommand]
    private Task OpenFileAsync(CommitFileChange? file)
    {
        if (file is null) return Task.CompletedTask;
        return _openFileDiff(Info.Sha, file);
    }
}

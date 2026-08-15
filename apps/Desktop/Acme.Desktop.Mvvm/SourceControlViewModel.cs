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

    public SourceControlViewModel(IServiceProvider serviceProvider, ICurrentProjectDirectoryProvider projectDirProvider)
    {
        _serviceProvider = serviceProvider;
        _projectDirProvider = projectDirProvider;
        _projectDirProvider.DirectoryChanged += OnProjectDirChanged;
        // 启动期若文档页已恢复上次项目，holder 已有值 → 立即同步一次。
        if (!string.IsNullOrEmpty(_projectDirProvider.CurrentDirectory))
            SetProjectAsync(_projectDirProvider.CurrentDirectory).SafeFireAndForget();
    }

    /// <summary>未暂存变更（工作区 vs index）。</summary>
    public ObservableCollection<ChangeItem> Changes { get; } = new();

    /// <summary>已暂存变更（index vs HEAD）。</summary>
    public ObservableCollection<ChangeItem> StagedChanges { get; } = new();

    /// <summary>当前展示的 diff（选中变更时填充）；null = 右栏空态。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasActiveDiff))]
    private DiffModel? _activeDiff;

    /// <summary>当前 diff 对应的文件路径（diff 头显示 + 丢弃按钮）。</summary>
    [ObservableProperty] private string? _activeDiffPath;

    [ObservableProperty] private string _commitMessage = string.Empty;

    /// <summary>是否打开了项目目录。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsNotRepository))]
    private bool _hasProject;

    /// <summary>打开的项目是否是 git 仓库。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsNotRepository))]
    private bool _isRepository;

    /// <summary>项目名（目录尾名）。</summary>
    [ObservableProperty] private string? _projectName;

    [ObservableProperty] private bool _isBusy;

    [ObservableProperty] private string? _statusMessage;

    /// <summary>「更改」段是否展开（折叠头）。</summary>
    [ObservableProperty] private bool _isChangesExpanded = true;

    /// <summary>「暂存的更改」段是否展开（折叠头）。</summary>
    [ObservableProperty] private bool _isStagedExpanded = true;

    // ── 历史模式（提交拓扑图）──

    /// <summary>是否在「历史」视图（false=「更改」工作区面板）。</summary>
    [ObservableProperty] private bool _isHistoryView;

    /// <summary>当前历史拓扑图（驱动图渲染）；切仓库/刷新时重载。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasHistory))]
    private CommitGraphModel? _historyGraph;

    /// <summary>选中的 commit（驱动右侧元信息 + 改动文件）。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelectedCommit))]
    private LaidOutCommit? _selectedCommit;

    /// <summary>选中 commit 的改动文件列表。</summary>
    public ObservableCollection<CommitFileChange> CommitFiles { get; } = new();

    /// <summary>选中 commit 某文件的 diff（驱动右侧 DiffView）。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasCommitDiff))]
    private DiffModel? _activeCommitDiff;

    [ObservableProperty] private string? _activeCommitDiffPath;

    /// <summary>能否提交：有暂存项且消息非空。</summary>
    public bool CanCommit => StagedChanges.Count > 0 && !string.IsNullOrWhiteSpace(CommitMessage);

    /// <summary>当前是否有 diff 展示（驱动右栏 diff/空态切换）。</summary>
    public bool HasActiveDiff => ActiveDiff is not null;

    /// <summary>打开了项目但不是 git 仓库（驱动「非仓库」空态；未开项目时为 false 走另一空态）。</summary>
    public bool IsNotRepository => HasProject && !IsRepository;

    /// <summary>历史图是否已加载。</summary>
    public bool HasHistory => HistoryGraph is not null && HistoryGraph.Commits.Count > 0;

    /// <summary>是否选中了 commit（驱动右侧元信息/空态）。</summary>
    public bool HasSelectedCommit => SelectedCommit is not null;

    /// <summary>是否有 commit 文件 diff 展示。</summary>
    public bool HasCommitDiff => ActiveCommitDiff is not null;

    private bool CanStageAll => Changes.Count > 0;

    partial void OnCommitMessageChanged(string value) => CommitCommand.NotifyCanExecuteChanged();

    [RelayCommand] private void ToggleChanges() => IsChangesExpanded = !IsChangesExpanded;
    [RelayCommand] private void ToggleStaged() => IsStagedExpanded = !IsStagedExpanded;

    /// <summary>切到「历史」视图时按需加载历史图。</summary>
    partial void OnIsHistoryViewChanged(bool value)
    {
        if (value && IsRepository && HistoryGraph is null)
            LoadHistoryAsync().SafeFireAndForget();
    }

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
            StatusMessage = HistoryGraph.Commits.Count == 0 ? "无提交" : $"{HistoryGraph.Commits.Count} 个提交";
        }
        catch (Exception ex) { StatusMessage = $"加载历史失败：{ex.Message}"; }
        finally { IsBusy = false; }
    }

    /// <summary>点 commit 节点 → 设选中 + 取其改动文件。</summary>
    [RelayCommand]
    private async Task SelectCommitAsync(LaidOutCommit? commit)
    {
        if (commit is null || string.IsNullOrEmpty(_projectDir)) return;
        SelectedCommit = commit;
        ActiveCommitDiff = null;
        ActiveCommitDiffPath = null;
        CommitFiles.Clear();
        var git = _serviceProvider.GetService<IGitService>();
        if (git is null) return;
        try
        {
            var files = await Task.Run(() => git.GetCommitChanges(_projectDir!, commit.Info.Sha));
            CommitFiles.Clear();
            foreach (var f in files) CommitFiles.Add(f);
        }
        catch { /* 忽略，列表空 */ }
    }

    /// <summary>点 commit 改动文件 → 取该文件 parent-vs-commit diff。</summary>
    [RelayCommand]
    private async Task OpenCommitFileDiffAsync(CommitFileChange? file)
    {
        if (file is null || SelectedCommit is null || string.IsNullOrEmpty(_projectDir)) return;
        var git = _serviceProvider.GetService<IGitService>();
        if (git is null) return;
        IsBusy = true;
        try
        {
            var diff = await Task.Run(() => git.GetCommitFileDiff(_projectDir!, SelectedCommit.Info.Sha, file.Path));
            ActiveCommitDiff = diff;
            ActiveCommitDiffPath = file.Path;
        }
        catch (Exception ex) { StatusMessage = $"读取差异失败：{ex.Message}"; }
        finally { IsBusy = false; }
    }

    /// <summary>项目目录变化（文档页打开/切换项目）→ 切仓库 + 刷新。</summary>
    private void OnProjectDirChanged(string? dir) => SetProjectAsync(dir).SafeFireAndForget();

    private async Task SetProjectAsync(string? dir)
    {
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
        SelectedCommit = null;
        CommitFiles.Clear();
        ActiveCommitDiff = null;
        ActiveCommitDiffPath = null;
        if (IsRepository)
        {
            if (IsHistoryView) await LoadHistoryAsync();
            else await RefreshAsync();
        }
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
        }
        catch (Exception ex) { StatusMessage = $"刷新失败：{ex.Message}"; }
        finally { IsBusy = false; }
    }

    /// <summary>点击变更行 → 取 diff 填 <see cref="ActiveDiff"/>。</summary>
    [RelayCommand]
    private async Task OpenDiffAsync(ChangeItem? item)
    {
        if (item is null || string.IsNullOrEmpty(_projectDir)) return;
        var git = _serviceProvider.GetService<IGitService>();
        if (git is null) return;
        IsBusy = true;
        try
        {
            var diff = await Task.Run(() => git.GetDiff(_projectDir, item.Path));
            ActiveDiff = diff;
            ActiveDiffPath = item.Path;
        }
        catch (Exception ex) { StatusMessage = $"读取差异失败：{ex.Message}"; }
        finally { IsBusy = false; }
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

    [RelayCommand(CanExecute = nameof(CanCommit))]
    private async Task CommitAsync()
    {
        if (!CanCommit) return;
        var git = _serviceProvider.GetService<IGitService>();
        if (git is null) return;
        try
        {
            var ok = await Task.Run(() => git.Commit(_projectDir!, CommitMessage.Trim()));
            StatusMessage = ok ? "已提交" : "提交失败";
            if (ok)
            {
                CommitMessage = string.Empty;
                HistoryGraph = null; // 新提交使历史图过期，下次进历史模式重载。
            }
            await RefreshAsync();
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

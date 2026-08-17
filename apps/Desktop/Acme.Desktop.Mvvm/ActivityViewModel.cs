using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using Aether.AI.Core.Conversations.Runs;
using Aether.AI.Memory;
using Acme.Desktop.Mvvm.Git;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.DependencyInjection;

namespace Acme.Desktop.Mvvm;

/// <summary>「活动」页 ViewModel：GitHub 式贡献热力图（近 N 天日历网格，颜色深浅=当天活动量）。
/// 数据=两者综合：Agent 执行（<see cref="IConversationStore.GetRunsInRangeAsync"/> 跨会话按时段查）+ Git 提交
///（<see cref="IGitService.GetHistory"/> 按日聚合，读文档页打开的项目目录）。点格看当天明细（runs + commits）；
/// 点 run 跳聊天页 resume（复用 <see cref="ChatWorkspaceViewModel.ResumeConversationCommand"/>）。UI-agnostic（零 Avalonia）。</summary>
public partial class ActivityViewModel : ObservableObject
{
    private readonly IServiceProvider _serviceProvider;
    private readonly ICurrentProjectDirectoryProvider _projectDirProvider;
    private readonly IUiDispatcher _dispatcher;
    private string? _projectDir;

    /// <summary>中国标准时区（UTC+8）——分桶日期 + UI 显示均硬编码 +8，不依赖系统时区（出差/海外开发机也准）。</summary>
    private static readonly TimeSpan ChinaOffset = TimeSpan.FromHours(8);

    // 已加载的按日分桶（一次加载同时供热力图计数 + 当天详情 filter，不重查）。
    private Dictionary<DateOnly, DayBucket> _buckets = new();

    public ObservableCollection<DayCell> Days { get; } = new();

    [ObservableProperty] private DayCell? _selectedDay;
    [ObservableProperty] private ActivityDayDetail? _dayDetail;
    [ObservableProperty] private bool _isLoading;
    [ObservableProperty] private ActivityRange _range = ActivityRange.Year;

    public ActivityViewModel(IServiceProvider serviceProvider, ICurrentProjectDirectoryProvider projectDirProvider)
    {
        _serviceProvider = serviceProvider;
        _projectDirProvider = projectDirProvider;
        _dispatcher = serviceProvider.GetRequiredService<IUiDispatcher>();
        _projectDirProvider.DirectoryChanged += OnProjectDirChanged;
        // 启动期若文档页已恢复上次项目，立即同步一次。
        if (!string.IsNullOrEmpty(_projectDirProvider.CurrentDirectory))
            SetProjectAsync(_projectDirProvider.CurrentDirectory).SafeFireAndForget();
    }

    private void OnProjectDirChanged(string? dir) => SetProjectAsync(dir).SafeFireAndForget();

    private async Task SetProjectAsync(string? dir)
    {
        _projectDir = dir;
        await LoadAsync();
    }

    // 切范围 → 重载网格。
    partial void OnRangeChanged(ActivityRange value) => LoadAsync().SafeFireAndForget();

    [RelayCommand]
    private void SetRange(ActivityRange range) => Range = range;

    public async Task LoadAsync()
    {
        IsLoading = true;
        try
        {
            var today = DateOnly.FromDateTime(DateTimeOffset.UtcNow.ToOffset(ChinaOffset).DateTime);
            var from = Range switch
            {
                ActivityRange.Month => today.AddDays(-29),
                ActivityRange.Quarter => today.AddDays(-89),
                _ => today.AddDays(-364),
            };
            // started_at 存 UTC ISO（AgentRun.StartedAt = DateTimeOffset.UtcNow）；查询边界统一转 UTC offset 0，
            // 字符串比较才严格等价时刻比较。from 用中国时区零点（硬编码 +8）转 UTC——不依赖系统时区。
            var fromDto = new DateTimeOffset(from.ToDateTime(TimeOnly.MinValue), ChinaOffset).ToUniversalTime();
            var toDto = DateTimeOffset.UtcNow;

            var store = _serviceProvider.GetService<IConversationStore>();
            var git = _serviceProvider.GetService<IGitService>();

            // Agent 维度：跨会话按时段查 run（userId 与聊天列表 GetSummariesAsync 同——桌面单用户空串）。
            var runs = store is null
                ? new List<RunSummary>()
                : (await store.GetRunsInRangeAsync(string.Empty, fromDto, toDto)).ToList();

            // Git 维度：仅当有项目目录且是仓库。
            var commits = new List<CommitInfo>();
            if (git is not null && !string.IsNullOrEmpty(_projectDir) && git.IsRepository(_projectDir))
            {
                var history = git.GetHistory(_projectDir, 5000);
                commits = history.Select(n => n.Info).Where(c => c.When >= fromDto).ToList();
            }

            // 按日分桶。
            var buckets = new Dictionary<DateOnly, DayBucket>();
            foreach (var r in runs)
            {
                var d = DateOnly.FromDateTime(r.StartedAt.ToOffset(ChinaOffset).DateTime);
                if (!buckets.TryGetValue(d, out var b)) buckets[d] = b = new DayBucket();
                b.Runs.Add(r);
            }
            foreach (var c in commits)
            {
                var d = DateOnly.FromDateTime(c.When.ToOffset(ChinaOffset).DateTime);
                if (!buckets.TryGetValue(d, out var b)) buckets[d] = b = new DayBucket();
                b.Commits.Add(c);
            }
            _buckets = buckets;

            // 构建网格 DayCell（GitHub 式 53 列×7 行：首列对齐到 from 的周日，按周列×周几行排；UniformGrid 按行填）。
            int dow = (int)from.DayOfWeek; // 0=Sunday..6=Saturday
            var cursor = from.AddDays(-dow);
            var cells = new List<DayCell>();
            while (cursor <= today)
            {
                var count = buckets.TryGetValue(cursor, out var bu) ? bu.Runs.Count + bu.Commits.Count : 0;
                var intensity = count switch { 0 => 0, <= 2 => 1, <= 5 => 2, <= 9 => 3, _ => 4 };
                var tip = count == 0 ? $"{cursor:yyyy-MM-dd}：无活动"
                                        : $"{cursor:yyyy-MM-dd}：{bu!.Runs.Count} 次执行 + {bu.Commits.Count} 次提交";
                cells.Add(new DayCell { Date = cursor, Intensity = intensity, Count = count, Tooltip = tip, IsToday = cursor == today });
                cursor = cursor.AddDays(1);
            }
            // 补齐到整周（最后一周填满到周六，避免 UniformGrid 末尾错位）。
            while (cells.Count % 7 != 0)
            {
                cursor = cursor.AddDays(1);
                cells.Add(new DayCell { Date = cursor, Intensity = 0, Count = 0, Tooltip = string.Empty });
            }

            // 重排成 GitHub 行优先序：UniformGrid Rows=7 按行填，第一行=所有周日横向排开（第 k 天 → 行 dow=k%7、列 week=k/7）。
            int weeks = cells.Count / 7;
            var ordered = new DayCell[cells.Count];
            for (int k = 0; k < cells.Count; k++)
                ordered[(k % 7) * weeks + (k / 7)] = cells[k];

            _dispatcher.Post(() =>
            {
                Days.Clear();
                foreach (var c in ordered) Days.Add(c);
                var todayCell = cells.FirstOrDefault(c => c.IsToday);
                if (todayCell is not null) SelectDay(todayCell);
                IsLoading = false;
            });
        }
        catch
        {
            _dispatcher.Post(() => IsLoading = false);
        }
    }

    [RelayCommand]
    private void SelectDay(DayCell? cell)
    {
        if (cell is null) return;
        foreach (var d in Days) d.IsSelected = false;
        cell.IsSelected = true;
        SelectedDay = cell;
        DayDetail = _buckets.TryGetValue(cell.Date, out var b)
            ? new ActivityDayDetail(cell.Date, b.Runs.ToList(), b.Commits.ToList())
            : new ActivityDayDetail(cell.Date, new(), new());
    }

    [RelayCommand]
    private void OpenRun(RunSummary? run)
    {
        if (run is null) return;
        // 运行时懒解析 shell VM（构造期 MainViewModelBase 尚未建完，避免循环依赖）。
        var chat = _serviceProvider.GetService<MainViewModelBase>()?.Chat;
        if (chat is null) return;
        var item = new ConversationItem
        {
            Id = run.ConversationId,
            Title = run.ConversationTitle,
            AgentId = run.AgentId,
            AgentName = run.AgentName,
            ResumeCommand = chat.ResumeConversationCommand,
        };
        chat.ResumeConversationCommand.Execute(item);
    }

    private sealed class DayBucket
    {
        public List<RunSummary> Runs { get; } = new();
        public List<CommitInfo> Commits { get; } = new();
    }
}

/// <summary>活动时间范围（近 30 天 / 近 90 天 / 近 1 年）。</summary>
public enum ActivityRange { Month, Quarter, Year }

/// <summary>热力图单格。Intensity 0-4（0=无活动，颜色映射在 view 层 converter）；IsSelected 可变触发选中边框刷新。</summary>
public sealed partial class DayCell : ObservableObject
{
    public DateOnly Date { get; init; }
    public int Intensity { get; init; }
    public int Count { get; init; }
    public string Tooltip { get; init; } = string.Empty;
    public bool IsToday { get; init; }
    [ObservableProperty] private bool _isSelected;
}

/// <summary>当天明细：agent runs + git commits（只读快照，从已加载桶构建）。</summary>
public sealed record ActivityDayDetail(DateOnly Date, List<RunSummary> Runs, List<CommitInfo> Commits);

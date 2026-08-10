using System.Collections.ObjectModel;
using Acme.Automation.Domain;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Acme.Desktop.Mvvm;

/// <summary>agent 选择项（agent 目录里的一项）。</summary>
public sealed class AgentOption : ObservableObject
{
    public Guid Id { get; init; }
    public string Name { get; init; } = string.Empty;
    public string Display => string.IsNullOrWhiteSpace(Name) ? "(未命名专家)" : Name;
}

/// <summary>周几选择项（1=周一..7=周日）。</summary>
public sealed class WeekdayOption : ObservableObject
{
    public int Day { get; init; }
    public string Label { get; init; } = string.Empty;
    public bool IsSelected { get; set; }
}

/// <summary>自动化任务行（UI 绑定用），镜像 <see cref="AutomationTask"/>。</summary>
public sealed partial class AutomationTaskRow : ObservableObject
{
    public int Id { get; set; }

    /// <summary>该任务的固定会话 Id（编辑/保存时透传，不在 UI 展示）。</summary>
    public Guid ConversationId { get; set; }

    [ObservableProperty] private string _taskName = string.Empty;
    [ObservableProperty] private string _prompt = string.Empty;
    [ObservableProperty] private AutomationScheduleType _selectedScheduleType = AutomationScheduleType.Daily;

    /// <summary>计划类型下拉项（编辑窗 ComboBox 用）。</summary>
    public AutomationScheduleType[] ScheduleTypeOptions { get; } = Enum.GetValues<AutomationScheduleType>();
    [ObservableProperty] private int _intervalMinutes = 60;
    [ObservableProperty] private string _timeOfDay = "09:30";
    [ObservableProperty] private bool _isEnabled = true;

    /// <summary>编辑窗 agent 下拉数据（每行一份拷贝，保证 ComboBox 选中工作）。</summary>
    public ObservableCollection<AgentOption> AvailableAgents { get; } = new();

    [ObservableProperty] private AgentOption? _selectedAgentOption;

    /// <summary>存储的 agent Guid 字符串（实体字段）。</summary>
    public string AgentId { get; set; } = string.Empty;

    /// <summary>列表卡片显示用的 agent 名（由 VM 解析填入）。</summary>
    [ObservableProperty] private string _agentName = string.Empty;

    public ObservableCollection<WeekdayOption> Weekdays { get; set; } = new();

    // 编辑窗条件面板可见性（避免引入枚举转换器）。
    public bool IsDaily => SelectedScheduleType == AutomationScheduleType.Daily;
    public bool IsInterval => SelectedScheduleType == AutomationScheduleType.Interval;
    public bool IsWeekly => SelectedScheduleType == AutomationScheduleType.Weekly;

    partial void OnSelectedScheduleTypeChanged(AutomationScheduleType value)
    {
        OnPropertyChanged(nameof(IsDaily));
        OnPropertyChanged(nameof(IsInterval));
        OnPropertyChanged(nameof(IsWeekly));
    }

    public DateTime? LastExecutionTime { get; set; }
    public string? LastExecutionStatus { get; set; }
    public string? LastExecutionResult { get; set; }
    public DateTime? NextExecutionTime { get; set; }

    partial void OnSelectedAgentOptionChanged(AgentOption? value)
        => AgentId = value?.Id.ToString() ?? string.Empty;

    public static readonly string[] WeekdayLabels =
        { "一", "二", "三", "四", "五", "六", "日" };

    /// <summary>构建一组周几选项（默认全不选）。</summary>
    public static ObservableCollection<WeekdayOption> CreateWeekdays(IEnumerable<int>? selected = null)
    {
        var set = selected is null ? null : new HashSet<int>(selected);
        var list = new ObservableCollection<WeekdayOption>();
        for (var d = 1; d <= 7; d++)
            list.Add(new WeekdayOption { Day = d, Label = WeekdayLabels[d - 1], IsSelected = set?.Contains(d) ?? false });
        return list;
    }

    public List<int> GetSelectedWeekdays()
        => Weekdays.Where(w => w.IsSelected).Select(w => w.Day).ToList();

    public string ScheduleDisplay => SelectedScheduleType switch
    {
        AutomationScheduleType.Daily => $"每天 {TimeOfDay}",
        AutomationScheduleType.Interval => $"每 {IntervalMinutes} 分钟",
        AutomationScheduleType.Weekly => $"每周 {WeekdaysDisplay} {TimeOfDay}",
        _ => SelectedScheduleType.ToString()
    };

    public string WeekdaysDisplay
    {
        get
        {
            var sel = GetSelectedWeekdays();
            return sel.Count == 0 ? "—" : string.Join("/", sel.Select(d => WeekdayLabels[d - 1]));
        }
    }

    public string StatusBadge => IsEnabled ? "已启用" : "已停用";

    public string NextRunDisplay => NextExecutionTime is null
        ? "—"
        : NextExecutionTime.Value.ToLocalTime().ToString("MM-dd HH:mm");

    public string LastRunDisplay
    {
        get
        {
            if (LastExecutionTime is null) return "未运行";
            var status = LastExecutionStatus switch
            {
                "Success" => "成功",
                "Failed" => "失败",
                _ => LastExecutionStatus ?? "—"
            };
            return $"{LastExecutionTime.Value.ToLocalTime():MM-dd HH:mm} · {status}";
        }
    }

    public string LastResultDisplay
    {
        get
        {
            if (string.IsNullOrWhiteSpace(LastExecutionResult)) return string.Empty;
            return LastExecutionResult.Length > 120
                ? LastExecutionResult[..120] + "…"
                : LastExecutionResult;
        }
    }

    /// <summary>从领域实体填充行（编辑/列表用）。</summary>
    public static AutomationTaskRow FromTask(AutomationTask t, IReadOnlyDictionary<Guid, string> agentNames)
    {
        var row = new AutomationTaskRow
        {
            Id = t.Id,
            ConversationId = t.ConversationId,
            TaskName = t.TaskName,
            Prompt = t.Prompt,
            SelectedScheduleType = t.ScheduleType,
            IntervalMinutes = t.IntervalMinutes <= 0 ? 60 : t.IntervalMinutes,
            TimeOfDay = t.ExecuteTime.ToString("HH:mm"),
            IsEnabled = t.Enabled,
            AgentId = t.AgentId,
            LastExecutionTime = t.LastExecutionTime,
            LastExecutionStatus = t.LastExecutionStatus,
            LastExecutionResult = t.LastExecutionResult,
            NextExecutionTime = t.NextExecutionTime,
            Weekdays = CreateWeekdays(t.Weekdays),
        };
        row.ResolveAgentName(agentNames);
        return row;
    }

    /// <summary>根据 AgentId 从目录解析显示名并回填 SelectedAgentOption。</summary>
    public void ResolveAgentName(IReadOnlyDictionary<Guid, string> agentNames)
    {
        AgentName = Guid.TryParse(AgentId, out var id) && agentNames.TryGetValue(id, out var n) ? n : "(已删除专家)";
    }

    /// <summary>就地刷新执行状态字段（保留列表选中/滚动位置），用于 TaskCompleted 局部更新。</summary>
    public void RefreshExecution(AutomationTask t)
    {
        LastExecutionTime = t.LastExecutionTime;
        LastExecutionStatus = t.LastExecutionStatus;
        LastExecutionResult = t.LastExecutionResult;
        NextExecutionTime = t.NextExecutionTime;
        IsEnabled = t.Enabled;
        // 上述为普通属性（非 [ObservableProperty]），手动 raise 派生显示属性以刷新 UI。
        OnPropertyChanged(nameof(NextRunDisplay));
        OnPropertyChanged(nameof(LastRunDisplay));
        OnPropertyChanged(nameof(LastResultDisplay));
        OnPropertyChanged(nameof(StatusBadge));
    }

    /// <summary>把行映射回领域实体（不含 Id/CreatedAt，由服务层补）。</summary>
    public AutomationTask ToTask()
    {
        return new AutomationTask
        {
            Id = Id,
            ConversationId = ConversationId,
            AgentId = AgentId,
            TaskName = TaskName,
            Prompt = Prompt,
            ScheduleType = SelectedScheduleType,
            IntervalMinutes = IntervalMinutes,
            ExecuteTime = SelectedScheduleType == AutomationScheduleType.Interval
                ? TimeOnly.MinValue
                : (TimeOnly.TryParse(TimeOfDay, out var t) ? t : new TimeOnly(9, 30)),
            Weekdays = GetSelectedWeekdays(),
            Enabled = IsEnabled,
        };
    }
}

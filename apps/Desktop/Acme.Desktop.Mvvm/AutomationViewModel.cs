using System.Collections.ObjectModel;
using Acme.Agents.AI;
using Acme.Automation.Application.Interfaces;
using Acme.Automation.Domain;
using Acme.Automation.Domain.Interfaces;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Acme.Desktop.Mvvm;

/// <summary>
/// 自动化页 ViewModel：管理定时任务（创建/编辑/删除/启停/立即运行）。
/// 镜像 <see cref="NotesViewModel"/> 的惰性服务 + UI 调度模式；订阅调度器完成事件刷新列表。
/// 仅 App 打开时生效（调度器为进程内 1 分钟轮询定时器）。
/// </summary>
public partial class AutomationViewModel : ObservableObject
{
    private readonly ICustomAgentCatalog _catalog;
    private readonly IAutomationService _service;
    private readonly IAutomationScheduler _scheduler;
    private readonly IUiDispatcher _dispatcher;

    private readonly Dictionary<Guid, string> _agentNames = new();
    private readonly List<AgentOption> _allAgents = new();

    public ObservableCollection<AutomationTaskRow> Tasks { get; } = new();

    [ObservableProperty] private string _statusMessage = string.Empty;
    [ObservableProperty] private bool _isBusy;

    public AutomationViewModel(
        ICustomAgentCatalog catalog,
        IAutomationService service,
        IAutomationScheduler scheduler,
        IUiDispatcher dispatcher)
    {
        _catalog = catalog;
        _service = service;
        _scheduler = scheduler;
        _dispatcher = dispatcher;

        _scheduler.TaskCompleted += OnTaskCompleted;
    }

    private void OnTaskCompleted(int taskId)
        => _dispatcher.Post(async () => await LoadAsync());

    /// <summary>加载专家目录 + 任务列表到 UI。</summary>
    public async Task LoadAsync()
    {
        try
        {
            // 专家目录
            var agents = await _catalog.ListAsync();
            _agentNames.Clear();
            _allAgents.Clear();
            foreach (var a in agents)
            {
                var name = string.IsNullOrWhiteSpace(a.DisplayName) ? a.Name : a.DisplayName;
                _agentNames[a.Id] = name;
                _allAgents.Add(new AgentOption { Id = a.Id, Name = name });
            }

            // 任务列表
            var tasks = await _service.GetAllTasksAsync(CancellationToken.None);

            await _dispatcher.InvokeAsync(() =>
            {
                Tasks.Clear();
                foreach (var t in tasks)
                {
                    var row = AutomationTaskRow.FromTask(t, _agentNames);
                    PopulateAgentOptions(row);
                    Tasks.Add(row);
                }
                StatusMessage = Tasks.Count == 0 ? "暂无任务，点「+ 创建任务」" : $"共 {Tasks.Count} 条任务";
            });
        }
        catch (Exception ex)
        {
            await _dispatcher.InvokeAsync(() => StatusMessage = $"加载失败：{ex.Message}");
        }
    }

    /// <summary>为行填充 agent 下拉项并回填选中（编辑窗用）。</summary>
    private void PopulateAgentOptions(AutomationTaskRow row)
    {
        row.AvailableAgents.Clear();
        foreach (var a in _allAgents)
            row.AvailableAgents.Add(new AgentOption { Id = a.Id, Name = a.Name });

        if (Guid.TryParse(row.AgentId, out var id))
            row.SelectedAgentOption = row.AvailableAgents.FirstOrDefault(o => o.Id == id);
        else
            row.SelectedAgentOption = row.AvailableAgents.FirstOrDefault();
    }

    /// <summary>构造一个空行用于「新建任务」编辑窗。</summary>
    public AutomationTaskRow CreateNewRow()
    {
        var row = new AutomationTaskRow
        {
            TaskName = "新任务",
            Prompt = string.Empty,
            SelectedScheduleType = AutomationScheduleType.Daily,
            IntervalMinutes = 60,
            TimeOfDay = "09:30",
            IsEnabled = true,
            Weekdays = AutomationTaskRow.CreateWeekdays(),
        };
        PopulateAgentOptions(row);
        return row;
    }

    /// <summary>为已存在任务构造可编辑行（编辑窗用）。</summary>
    public AutomationTaskRow CreateEditRow(AutomationTaskRow source)
    {
        var row = new AutomationTaskRow
        {
            Id = source.Id,
            ConversationId = source.ConversationId,
            TaskName = source.TaskName,
            Prompt = source.Prompt,
            SelectedScheduleType = source.SelectedScheduleType,
            IntervalMinutes = source.IntervalMinutes,
            TimeOfDay = source.TimeOfDay,
            IsEnabled = source.IsEnabled,
            AgentId = source.AgentId,
            Weekdays = AutomationTaskRow.CreateWeekdays(source.GetSelectedWeekdays()),
        };
        PopulateAgentOptions(row);
        return row;
    }

    [RelayCommand]
    private async Task SaveTaskAsync(AutomationTaskRow row)
    {
        if (row is null) return;
        if (row.SelectedAgentOption is null)
        {
            StatusMessage = "请选择要触发的专家";
            return;
        }
        if (string.IsNullOrWhiteSpace(row.TaskName))
        {
            StatusMessage = "请填写任务名";
            return;
        }

        try
        {
            IsBusy = true;
            var task = row.ToTask();
            if (row.Id == 0)
            {
                var created = await _service.CreateTaskAsync(task, CancellationToken.None);
                StatusMessage = $"已创建「{created.TaskName}」";
            }
            else
            {
                await _service.UpdateTaskAsync(task, CancellationToken.None);
                StatusMessage = $"已更新「{task.TaskName}」";
            }
            await LoadAsync();
        }
        catch (Exception ex)
        {
            StatusMessage = $"保存失败：{ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task DeleteTaskAsync(AutomationTaskRow row)
    {
        if (row is null || row.Id == 0) return;
        try
        {
            await _service.DeleteTaskAsync(row.Id, CancellationToken.None);
            StatusMessage = $"已删除「{row.TaskName}」";
            await LoadAsync();
        }
        catch (Exception ex)
        {
            StatusMessage = $"删除失败：{ex.Message}";
        }
    }

    [RelayCommand]
    private async Task ToggleEnabledAsync(AutomationTaskRow row)
    {
        if (row is null || row.Id == 0) return;
        try
        {
            row.IsEnabled = !row.IsEnabled;
            var task = row.ToTask();
            await _service.UpdateTaskAsync(task, CancellationToken.None);
            StatusMessage = row.IsEnabled ? $"已启用「{row.TaskName}」" : $"已停用「{row.TaskName}」";
            await LoadAsync();
        }
        catch (Exception ex)
        {
            StatusMessage = $"操作失败：{ex.Message}";
            await LoadAsync();
        }
    }

    [RelayCommand]
    private async Task RunNowAsync(AutomationTaskRow row)
    {
        if (row is null || row.Id == 0) return;
        try
        {
            StatusMessage = $"正在运行「{row.TaskName}」…";
            await _scheduler.RunNowAsync(row.Id, CancellationToken.None);
            // 完成后 TaskCompleted 事件会触发 LoadAsync；这里先乐观刷新状态显示
        }
        catch (Exception ex)
        {
            StatusMessage = $"运行失败：{ex.Message}";
        }
    }
}

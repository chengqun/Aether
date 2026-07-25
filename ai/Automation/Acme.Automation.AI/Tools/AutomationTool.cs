using System.ComponentModel;
using Acme.Automation.Application.Interfaces;
using Acme.Automation.Domain;

namespace Acme.Automation.AI.Tools;

/// <summary>
/// Automation tools exposed to the agent: manage scheduled automation tasks (CRUD) and
/// start/stop the scheduler. Wraps <see cref="IAutomationService"/> — same shape as
/// <c>Acme.Notes.AI.NoteTool</c> wraps <c>INoteService</c>.
/// </summary>
public class AutomationTool
{
    private readonly IAutomationService _service;

    public AutomationTool(IAutomationService service)
    {
        _service = service;
    }

    public class TaskSummary
    {
        public int Id { get; set; }
        public string? AgentId { get; set; }
        public string? TaskName { get; set; }
        public string? ScheduleType { get; set; }
        public bool Enabled { get; set; }
        public DateTime? NextExecutionTime { get; set; }
        public string? LastExecutionStatus { get; set; }
    }

    public class ListOutput
    {
        public List<TaskSummary> Tasks { get; set; } = new();
        public string? Error { get; set; }
    }

    public class ActionOutput
    {
        public bool Success { get; set; }
        public int? Id { get; set; }
        public string? Error { get; set; }
    }

    [Description("列出所有自动化任务。")]
    public async Task<ListOutput> ListAutomationTasksAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var tasks = await _service.GetAllTasksAsync(cancellationToken);
            return new ListOutput { Tasks = tasks.Select(ToSummary).ToList() };
        }
        catch (Exception ex)
        {
            return new ListOutput { Error = $"列出任务失败: {ex.Message}" };
        }
    }

    [Description("新建一条自动化任务，按计划触发指定 agent。scheduleType: Daily/Interval/Weekly。")]
    public async Task<ActionOutput> CreateAutomationTaskAsync(
        [Description("要触发的 agentId")] string agentId,
        [Description("任务名")] string taskName,
        [Description("计划类型：Daily/Interval/Weekly")] string scheduleType,
        [Description("Daily/Weekly 的执行时间 HH:mm:ss（如 09:30:00），Interval 留空")] string? executeTime = null,
        [Description("Interval 类型的间隔分钟数，其它类型留空")] int intervalMinutes = 0,
        [Description("是否启用，默认 true")] bool enabled = true,
        CancellationToken cancellationToken = default)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(agentId) || string.IsNullOrWhiteSpace(taskName))
                return new ActionOutput { Error = "agentId 和 taskName 不能为空" };
            if (!Enum.TryParse<AutomationScheduleType>(scheduleType, ignoreCase: true, out var st))
                return new ActionOutput { Error = $"无效的 scheduleType: {scheduleType}（Daily/Interval/Weekly）" };

            var task = new AutomationTask
            {
                AgentId = agentId,
                TaskName = taskName,
                ScheduleType = st,
                IntervalMinutes = intervalMinutes,
                ExecuteTime = st == AutomationScheduleType.Interval
                    ? TimeOnly.MinValue
                    : (TimeOnly.TryParse(executeTime, out var t) ? t : TimeOnly.MinValue),
                Enabled = enabled,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow,
            };
            var created = await _service.CreateTaskAsync(task, cancellationToken);
            return new ActionOutput { Success = true, Id = created.Id };
        }
        catch (Exception ex)
        {
            return new ActionOutput { Error = $"创建任务失败: {ex.Message}" };
        }
    }

    [Description("按 id 删除一条自动化任务。")]
    public async Task<ActionOutput> DeleteAutomationTaskAsync(
        [Description("任务 id")] int id,
        CancellationToken cancellationToken = default)
    {
        try
        {
            await _service.DeleteTaskAsync(id, cancellationToken);
            return new ActionOutput { Success = true, Id = id };
        }
        catch (Exception ex)
        {
            return new ActionOutput { Error = $"删除任务失败: {ex.Message}" };
        }
    }

    [Description("启动自动化调度器（开始按计划触发任务）。")]
    public async Task<ActionOutput> StartSchedulerAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            await _service.StartSchedulerAsync(cancellationToken);
            return new ActionOutput { Success = true };
        }
        catch (Exception ex)
        {
            return new ActionOutput { Error = $"启动调度器失败: {ex.Message}" };
        }
    }

    [Description("停止自动化调度器。")]
    public async Task<ActionOutput> StopSchedulerAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            await _service.StopSchedulerAsync(cancellationToken);
            return new ActionOutput { Success = true };
        }
        catch (Exception ex)
        {
            return new ActionOutput { Error = $"停止调度器失败: {ex.Message}" };
        }
    }

    private static TaskSummary ToSummary(AutomationTask t) => new()
    {
        Id = t.Id,
        AgentId = t.AgentId,
        TaskName = t.TaskName,
        ScheduleType = t.ScheduleType.ToString(),
        Enabled = t.Enabled,
        NextExecutionTime = t.NextExecutionTime,
        LastExecutionStatus = t.LastExecutionStatus,
    };
}

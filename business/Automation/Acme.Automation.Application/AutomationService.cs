using Acme.Automation.Application.Interfaces;
using Acme.Automation.Domain;
using Acme.Automation.Domain.Interfaces;

namespace Acme.Automation.Application;

public class AutomationService : IAutomationService
{
    private readonly IAutomationTaskRepository _taskRepository;
    private readonly IAutomationScheduler _scheduler;

    public AutomationService(IAutomationTaskRepository taskRepository, IAutomationScheduler scheduler)
    {
        _taskRepository = taskRepository;
        _scheduler = scheduler;
    }

    public Task<List<AutomationTask>> GetAllTasksAsync(CancellationToken cancellationToken)
        => _taskRepository.GetAllAsync(cancellationToken);

    public Task<AutomationTask?> GetTaskByIdAsync(int id, CancellationToken cancellationToken)
        => _taskRepository.GetByIdAsync(id, cancellationToken);

    public async Task<AutomationTask> CreateTaskAsync(AutomationTask task, CancellationToken cancellationToken)
    {
        task.CreatedAt = DateTime.UtcNow;
        task.UpdatedAt = DateTime.UtcNow;
        task.NextExecutionTime = CalculateNextExecutionTime(task);
        return await _taskRepository.AddAsync(task, cancellationToken);
    }

    public async Task<AutomationTask> UpdateTaskAsync(AutomationTask task, CancellationToken cancellationToken)
    {
        task.UpdatedAt = DateTime.UtcNow;
        task.NextExecutionTime = CalculateNextExecutionTime(task);
        return await _taskRepository.UpdateAsync(task, cancellationToken);
    }

    public Task DeleteTaskAsync(int id, CancellationToken cancellationToken)
        => _taskRepository.DeleteAsync(id, cancellationToken);

    public Task StartSchedulerAsync(CancellationToken cancellationToken)
        => _scheduler.StartAsync(cancellationToken);

    public Task StopSchedulerAsync(CancellationToken cancellationToken)
        => _scheduler.StopAsync(cancellationToken);

    private static DateTime? CalculateNextExecutionTime(AutomationTask task)
    {
        if (!task.Enabled)
            return null;

        var now = DateTime.UtcNow;

        return task.ScheduleType switch
        {
            AutomationScheduleType.Daily => CalculateDailyNextTime(task.ExecuteTime, now),
            AutomationScheduleType.Interval => now.AddMinutes(task.IntervalMinutes),
            AutomationScheduleType.Weekly => CalculateWeeklyNextTime(task.ExecuteTime, now),
            _ => null
        };
    }

    private static DateTime CalculateDailyNextTime(TimeOnly executeTime, DateTime now)
    {
        var todayAtExecuteTime = now.Date.Add(executeTime.ToTimeSpan());
        return todayAtExecuteTime > now ? todayAtExecuteTime : todayAtExecuteTime.AddDays(1);
    }

    private static DateTime CalculateWeeklyNextTime(TimeOnly executeTime, DateTime now)
    {
        var todayAtExecuteTime = now.Date.Add(executeTime.ToTimeSpan());
        if (todayAtExecuteTime > now)
            return todayAtExecuteTime;
        return todayAtExecuteTime.AddDays(7);
    }
}
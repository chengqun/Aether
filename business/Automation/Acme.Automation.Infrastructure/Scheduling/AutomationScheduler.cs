using Acme.Automation.Application.Interfaces;
using Acme.Automation.Domain;
using Acme.Automation.Domain.Interfaces;

namespace Acme.Automation.Infrastructure.Scheduling;

public class AutomationScheduler : IAutomationScheduler
{
    private readonly IAutomationTaskRepository _taskRepository;
    private readonly IAgentExecutor _agentExecutor;
    private Timer? _timer;
    private CancellationTokenSource? _cts;

    public AutomationScheduler(IAutomationTaskRepository taskRepository, IAgentExecutor agentExecutor)
    {
        _taskRepository = taskRepository;
        _agentExecutor = agentExecutor;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _timer = new Timer(CheckAndExecuteTasks, null, TimeSpan.Zero, TimeSpan.FromMinutes(1));
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _timer?.Change(Timeout.Infinite, Timeout.Infinite);
        _timer?.Dispose();
        _cts?.Cancel();
        _cts?.Dispose();
        return Task.CompletedTask;
    }

    private async void CheckAndExecuteTasks(object? state)
    {
        try
        {
            var tasks = await _taskRepository.GetDueTasksAsync(_cts?.Token ?? CancellationToken.None);
            foreach (var task in tasks)
                await ExecuteTaskAsync(task);
        }
        catch (Exception)
        {
        }
    }

    private async Task ExecuteTaskAsync(AutomationTask task)
    {
        var now = DateTime.UtcNow;

        try
        {
            var result = await _agentExecutor.ExecuteAsync(task.AgentId, $"执行自动化任务: {task.TaskName}", _cts?.Token ?? CancellationToken.None);

            task.LastExecutionTime = now;
            task.LastExecutionStatus = "Success";
            task.LastExecutionResult = result;
            task.NextExecutionTime = CalculateNextExecutionTime(task, now);
        }
        catch (Exception ex)
        {
            task.LastExecutionTime = now;
            task.LastExecutionStatus = "Failed";
            task.LastExecutionResult = ex.Message;
            task.NextExecutionTime = CalculateNextExecutionTime(task, now);
        }

        await _taskRepository.UpdateAsync(task, _cts?.Token ?? CancellationToken.None);
    }

    private static DateTime? CalculateNextExecutionTime(AutomationTask task, DateTime lastExecutionTime)
    {
        if (!task.Enabled)
            return null;

        return task.ScheduleType switch
        {
            AutomationScheduleType.Daily => CalculateDailyNextTime(task.ExecuteTime, lastExecutionTime),
            AutomationScheduleType.Interval => lastExecutionTime.AddMinutes(task.IntervalMinutes),
            AutomationScheduleType.Weekly => CalculateWeeklyNextTime(task.ExecuteTime, lastExecutionTime),
            _ => null
        };
    }

    private static DateTime CalculateDailyNextTime(TimeOnly executeTime, DateTime lastExecutionTime)
    {
        var todayAtExecuteTime = lastExecutionTime.Date.Add(executeTime.ToTimeSpan());
        if (todayAtExecuteTime > lastExecutionTime)
            return todayAtExecuteTime;
        return todayAtExecuteTime.AddDays(1);
    }

    private static DateTime CalculateWeeklyNextTime(TimeOnly executeTime, DateTime lastExecutionTime)
    {
        var todayAtExecuteTime = lastExecutionTime.Date.Add(executeTime.ToTimeSpan());
        if (todayAtExecuteTime > lastExecutionTime)
            return todayAtExecuteTime;
        return todayAtExecuteTime.AddDays(7);
    }
}
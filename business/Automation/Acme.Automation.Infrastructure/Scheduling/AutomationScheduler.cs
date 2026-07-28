using Acme.Automation.Domain;
using Acme.Automation.Domain.Interfaces;

namespace Acme.Automation.Infrastructure.Scheduling;

public class AutomationScheduler : IAutomationScheduler
{
    private readonly IAutomationTaskRepository _taskRepository;
    private readonly IAgentExecutor _agentExecutor;
    private Timer? _timer;
    private CancellationTokenSource? _cts;

    public event Action<int>? TaskCompleted;

    public AutomationScheduler(IAutomationTaskRepository taskRepository, IAgentExecutor agentExecutor)
    {
        _taskRepository = taskRepository;
        _agentExecutor = agentExecutor;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        // 双启动守卫：已运行则不重复创建定时器。
        if (_timer is not null) return Task.CompletedTask;

        _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _timer = new Timer(CheckAndExecuteTasks, null, TimeSpan.Zero, TimeSpan.FromMinutes(1));
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _timer?.Change(Timeout.Infinite, Timeout.Infinite);
        _timer?.Dispose();
        _timer = null;
        _cts?.Cancel();
        _cts?.Dispose();
        _cts = null;
        return Task.CompletedTask;
    }

    public async Task RunNowAsync(int taskId, CancellationToken cancellationToken = default)
    {
        var task = await _taskRepository.GetByIdAsync(taskId, cancellationToken);
        if (task is null) return;
        await RunTaskAsync(task, cancellationToken);
    }

    private async void CheckAndExecuteTasks(object? state)
    {
        try
        {
            var tasks = await _taskRepository.GetDueTasksAsync(_cts?.Token ?? CancellationToken.None);
            foreach (var task in tasks)
                await RunTaskAsync(task, _cts?.Token ?? CancellationToken.None);
        }
        catch (Exception)
        {
            // 定时线程不可因单轮异常中断；具体任务异常已在 RunTaskAsync 内记录。
        }
    }

    private async Task RunTaskAsync(AutomationTask task, CancellationToken cancellationToken)
    {
        var now = DateTime.UtcNow;
        var message = string.IsNullOrWhiteSpace(task.Prompt)
            ? $"执行自动化任务: {task.TaskName}"
            : task.Prompt;

        try
        {
            var result = await _agentExecutor.ExecuteAsync(
                task.AgentId, task.ConversationId, task.TaskName, message, cancellationToken);

            task.LastExecutionTime = now;
            task.LastExecutionStatus = "Success";
            task.LastExecutionResult = result;
            task.NextExecutionTime = AutomationScheduleCalculator.ComputeNextRun(task, now);
        }
        catch (Exception ex)
        {
            task.LastExecutionTime = now;
            task.LastExecutionStatus = "Failed";
            task.LastExecutionResult = ex.Message;
            task.NextExecutionTime = AutomationScheduleCalculator.ComputeNextRun(task, now);
        }

        try
        {
            await _taskRepository.UpdateAsync(task, cancellationToken);
        }
        catch (Exception)
        {
            // 持久化失败不应吞掉完成事件。
        }
        finally
        {
            TaskCompleted?.Invoke(task.Id);
        }
    }
}

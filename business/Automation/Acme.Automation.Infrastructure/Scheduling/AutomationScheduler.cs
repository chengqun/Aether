using Acme.Automation.Domain;
using Acme.Automation.Domain.Interfaces;

namespace Acme.Automation.Infrastructure.Scheduling;

public class AutomationScheduler : IAutomationScheduler
{
    private readonly IAutomationTaskRepository _taskRepository;
    private readonly IAgentExecutor _agentExecutor;
    private Timer? _timer;
    private CancellationTokenSource? _cts;
    // P1 守卫：串行化所有任务执行——Timer 回调(async void)+ RunNowAsync 共用此锁，避免同任务
    // (同 ConversationId) 并发跑两次 → run seq TOCTOU → resume-400。app 生命周期单例，GC 回收不显式 Dispose。
    private readonly SemaphoreSlim _executingLock = new(1, 1);

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
        // P1 守卫：与定时轮询共用 _executingLock。手动触发撞上在跑的轮询则抛错，UI 显示「运行失败」。
        if (!await _executingLock.WaitAsync(0, cancellationToken))
            throw new InvalidOperationException("调度器正在执行任务，请稍后再试");
        try
        {
            var task = await _taskRepository.GetByIdAsync(taskId, cancellationToken);
            if (task is null) return;
            await RunTaskAsync(task, cancellationToken);
        }
        finally
        {
            _executingLock.Release();
        }
    }

    private async void CheckAndExecuteTasks(object? state)
    {
        // P1 守卫：Timer 回调是 async void，ExecuteAsync 可能耗时>1min，下个 tick 重入会重取同任务
        // 并发跑（同 ConversationId → seq TOCTOU）。try-acquire(0)：在跑则跳过本轮，不等不堆积。
        if (!await _executingLock.WaitAsync(0))
            return;
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
        finally
        {
            _executingLock.Release();
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

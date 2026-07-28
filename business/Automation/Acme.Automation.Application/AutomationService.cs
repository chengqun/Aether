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
        // 每任务固定一个会话 Id，后续触发复用同一条会话（进最近会话 + 跨次记忆）。
        if (task.ConversationId == Guid.Empty)
            task.ConversationId = Guid.NewGuid();
        task.SerializeWeekdays();
        task.NextExecutionTime = AutomationScheduleCalculator.ComputeNextRun(task, DateTime.UtcNow);
        return await _taskRepository.AddAsync(task, cancellationToken);
    }

    public async Task<AutomationTask> UpdateTaskAsync(AutomationTask task, CancellationToken cancellationToken)
    {
        task.UpdatedAt = DateTime.UtcNow;
        task.SerializeWeekdays();
        task.NextExecutionTime = AutomationScheduleCalculator.ComputeNextRun(task, DateTime.UtcNow);
        return await _taskRepository.UpdateAsync(task, cancellationToken);
    }

    public Task DeleteTaskAsync(int id, CancellationToken cancellationToken)
        => _taskRepository.DeleteAsync(id, cancellationToken);

    public Task StartSchedulerAsync(CancellationToken cancellationToken)
        => _scheduler.StartAsync(cancellationToken);

    public Task StopSchedulerAsync(CancellationToken cancellationToken)
        => _scheduler.StopAsync(cancellationToken);
}
using Acme.Automation.Domain;

namespace Acme.Automation.Application.Interfaces;

public interface IAutomationService
{
    Task<List<AutomationTask>> GetAllTasksAsync(CancellationToken cancellationToken);
    Task<AutomationTask?> GetTaskByIdAsync(int id, CancellationToken cancellationToken);
    Task<AutomationTask> CreateTaskAsync(AutomationTask task, CancellationToken cancellationToken);
    Task<AutomationTask> UpdateTaskAsync(AutomationTask task, CancellationToken cancellationToken);
    Task DeleteTaskAsync(int id, CancellationToken cancellationToken);
    Task StartSchedulerAsync(CancellationToken cancellationToken);
    Task StopSchedulerAsync(CancellationToken cancellationToken);
}
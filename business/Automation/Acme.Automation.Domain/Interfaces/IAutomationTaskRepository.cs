namespace Acme.Automation.Domain.Interfaces;

public interface IAutomationTaskRepository
{
    Task<List<AutomationTask>> GetAllAsync(CancellationToken cancellationToken);
    Task<AutomationTask?> GetByIdAsync(int id, CancellationToken cancellationToken);
    Task<AutomationTask> AddAsync(AutomationTask task, CancellationToken cancellationToken);
    Task<AutomationTask> UpdateAsync(AutomationTask task, CancellationToken cancellationToken);
    Task DeleteAsync(int id, CancellationToken cancellationToken);
    Task<List<AutomationTask>> GetDueTasksAsync(CancellationToken cancellationToken);
}
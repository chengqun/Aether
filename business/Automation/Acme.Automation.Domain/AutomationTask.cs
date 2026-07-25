namespace Acme.Automation.Domain;

public class AutomationTask
{
    public int Id { get; set; }
    public string AgentId { get; set; } = string.Empty;
    public string TaskName { get; set; } = string.Empty;
    public AutomationScheduleType ScheduleType { get; set; }
    public int IntervalMinutes { get; set; }
    public TimeOnly ExecuteTime { get; set; }
    public bool Enabled { get; set; }
    public DateTime? LastExecutionTime { get; set; }
    public string? LastExecutionStatus { get; set; }
    public string? LastExecutionResult { get; set; }
    public DateTime? NextExecutionTime { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}
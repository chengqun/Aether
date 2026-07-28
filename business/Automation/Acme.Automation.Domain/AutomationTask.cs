using System.Text.Json;

namespace Acme.Automation.Domain;

public class AutomationTask
{
    public int Id { get; set; }
    public string AgentId { get; set; } = string.Empty;
    public string TaskName { get; set; } = string.Empty;

    /// <summary>该任务固定的会话 Id。触发时复用同一条会话 → 进「最近会话」+ 跨次记忆。</summary>
    public Guid ConversationId { get; set; }

    /// <summary>触发时发给 agent 的提示词；为空则回退到「执行自动化任务: {TaskName}」。</summary>
    public string Prompt { get; set; } = string.Empty;

    public AutomationScheduleType ScheduleType { get; set; }
    public int IntervalMinutes { get; set; }
    public TimeOnly ExecuteTime { get; set; }

    /// <summary>Weekly 选中周几（1=周一..7=周日）。非持久化，序列化为 <see cref="WeekdaysJson"/>。</summary>
    public List<int> Weekdays { get; set; } = new();

    /// <summary><see cref="Weekdays"/> 的 JSON 持久化字段。</summary>
    public string? WeekdaysJson { get; set; }

    public bool Enabled { get; set; }
    public DateTime? LastExecutionTime { get; set; }
    public string? LastExecutionStatus { get; set; }
    public string? LastExecutionResult { get; set; }
    public DateTime? NextExecutionTime { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }

    /// <summary>把 <see cref="Weekdays"/> 序列化为 <see cref="WeekdaysJson"/>。</summary>
    public void SerializeWeekdays()
        => WeekdaysJson = Weekdays.Count == 0 ? null : JsonSerializer.Serialize(Weekdays);

    /// <summary>从 <see cref="WeekdaysJson"/> 反序列化填充 <see cref="Weekdays"/>。</summary>
    public void DeserializeWeekdays()
    {
        Weekdays = string.IsNullOrWhiteSpace(WeekdaysJson)
            ? new()
            : JsonSerializer.Deserialize<List<int>>(WeekdaysJson) ?? new();
    }
}
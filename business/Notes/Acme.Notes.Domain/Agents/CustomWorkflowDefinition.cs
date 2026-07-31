namespace Acme.Notes.Domain.Agents;

/// <summary>workflow 转交边（持久化用，与 Core 的 WorkflowHandoff 平行，business 不引 Aether.AI.*）。</summary>
public sealed class WorkflowHandoffField
{
    public Guid FromAgentId { get; set; }
    public Guid ToAgentId { get; set; }
    public string? Reason { get; set; }
}

/// <summary>
/// UI 无关的自定义 Workflow 定义（目录数据模型）。持久化为 Notes 的
/// <c>NoteNodeType.Workflow</c> 笔记（<c>WorkflowSourceParser</c> JSON 格式）。
/// </summary>
public sealed class CustomWorkflowDefinition
{
    public Guid Id { get; set; }

    public string Name { get; set; } = string.Empty;

    public string Description { get; set; } = string.Empty;

    public string Category { get; set; } = "工作流";

    /// <summary>入口 agent Id（须在 Participants 内）。</summary>
    public Guid EntryAgentId { get; set; }

    /// <summary>参与者 agent Id 列表（含入口）。</summary>
    public IReadOnlyList<Guid> Participants { get; set; } = Array.Empty<Guid>();

    /// <summary>转交边。</summary>
    public IReadOnlyList<WorkflowHandoffField> Handoffs { get; set; } = Array.Empty<WorkflowHandoffField>();

    public string? HandoffInstructions { get; set; }
}

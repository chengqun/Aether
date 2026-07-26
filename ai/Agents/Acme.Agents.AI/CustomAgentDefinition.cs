using Acme.Notes.AgentSource;

namespace Acme.Agents.AI;

/// <summary>
/// UI 无关的自定义 Agent 定义（目录数据模型）。持久化为 Notes 的
/// <c>NoteNodeType.Agent</c> 笔记（<see cref="AgentSourceParser"/> 格式）。
/// 与平台运行时 <c>AgentDefinition</c> 的映射由 <see cref="ICustomAgentRunner"/> 完成。
/// </summary>
public sealed class CustomAgentDefinition
{
    /// <summary>稳定标识（对应 Note 的 Id）。</summary>
    public Guid Id { get; set; }

    public string Name { get; set; } = string.Empty;

    public string Description { get; set; } = string.Empty;

    public string Category { get; set; } = "普通对话";

    /// <summary>系统提示词。</summary>
    public string SystemPrompt { get; set; } = string.Empty;

    /// <summary>勾选启用的工具名（对应 ToolRegistry 中的工具）。</summary>
    public IReadOnlyList<string> Tools { get; set; } = Array.Empty<string>();

    /// <summary>记忆模式（当前未由运行时强制，仅持久化；见计划缺口）。</summary>
    public AgentMemoryMode Memory { get; set; } = AgentMemoryMode.SlidingWindow;

    public int MaxHistory { get; set; } = 20;
}
